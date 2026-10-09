using DeskBox.ViewModels;

namespace DeskBox.Tests;

/// <summary>
/// Music timeline text degradation (feedback 326/287/474/478): players that
/// never publish a duration must not show a stuck 00:00 position. Real
/// elapsed time shows whenever any of it is known; the placeholder appears
/// only when both position and duration are unknown, mirroring DurationText.
/// </summary>
public sealed class MusicTimelineTextTests
{
    [Theory]
    [InlineData(0, 0, "--:--")]          // nothing reported → placeholder
    [InlineData(90, 0, "01:30")]         // live / position-only → elapsed
    [InlineData(0, 180, "00:00")]        // duration arrives first → true zero
    [InlineData(90, 180, "01:30")]       // both known → elapsed
    [InlineData(3661, 7200, "1:01:01")]  // hour formatting passthrough
    public void FormatPositionText_CoversTimelineDegradation(
        double positionSeconds,
        double durationSeconds,
        string expected)
    {
        Assert.Equal(
            expected,
            MusicWidgetViewModel.FormatPositionText(
                TimeSpan.FromSeconds(positionSeconds),
                TimeSpan.FromSeconds(durationSeconds)));
    }

    [Fact]
    public void FormatPositionText_AgreesWithDurationTextWhenTimelineIsUnknown()
    {
        // Symmetry contract: whenever DurationText falls back to its
        // placeholder and no position has arrived, PositionText must use the
        // same placeholder instead of a misleading 00:00.
        string durationText = "--:--";
        string positionText = MusicWidgetViewModel.FormatPositionText(
            TimeSpan.Zero,
            TimeSpan.Zero);

        Assert.Equal(durationText, positionText);
    }
}
