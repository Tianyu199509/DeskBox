using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class QuickRevealTrayRaisePolicyTests
{
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void KeepsRaisedStateOnTaskbarForeground_onlyForTrayIconQuickRevealRaises(
        bool isQuickRevealMode,
        bool raisedFromTrayIcon,
        bool expected)
    {
        Assert.Equal(
            expected,
            QuickRevealTrayRaisePolicy.KeepsRaisedStateOnTaskbarForeground(
                isQuickRevealMode,
                raisedFromTrayIcon));
    }

    [Theory]
    [InlineData(true, true, true, 0.0, true)]
    [InlineData(true, true, true, 150.0, true)]
    [InlineData(true, true, true, 400.0, true)]
    [InlineData(true, true, true, 400.5, false)]
    [InlineData(true, true, true, -1.0, false)]
    [InlineData(false, true, true, 100.0, false)]
    [InlineData(true, false, true, 100.0, false)]
    [InlineData(true, true, false, 100.0, false)]
    public void ShouldSuppressTrayRaise_requiresQuickRevealTrayIconTaskbarDismissWithinCooldown(
        bool isQuickRevealMode,
        bool isTrayIconSource,
        bool lastDismissTaskbarOrigin,
        double millisecondsSinceLastDismiss,
        bool expected)
    {
        Assert.Equal(
            expected,
            QuickRevealTrayRaisePolicy.ShouldSuppressTrayRaise(
                isQuickRevealMode,
                isTrayIconSource,
                lastDismissTaskbarOrigin,
                millisecondsSinceLastDismiss));
    }

    [Fact]
    public void ShouldSuppressTrayRaise_staleDismissalOutsideCooldownDoesNotSuppress()
    {
        Assert.False(
            QuickRevealTrayRaisePolicy.ShouldSuppressTrayRaise(
                isQuickRevealMode: true,
                isTrayIconSource: true,
                lastDismissTaskbarOrigin: true,
                millisecondsSinceLastDismiss:
                    QuickRevealTrayRaisePolicy.TaskbarPressDismissCooldownMilliseconds + 1000.0));
    }
}
