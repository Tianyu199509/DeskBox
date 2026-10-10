namespace DeskBox.Tests;

using DeskBox.Models;

/// <summary>
/// Budget curve of the group-switch first-frame gate (feedback 497): the old
/// fixed 900ms deadline made file-heavy members deterministically unreachable
/// — the switch always hit "first-frame wait timed out; timeout-rollback".
/// The budget must scale with the projected item count, keep the historical
/// floor for ordinary members, and cap the wait so a dead surface cannot
/// stall the switch gate unboundedly.
/// </summary>
public sealed class WidgetGroupFirstFrameBudgetPolicyTests
{
    [Theory]
    [InlineData(0, 900)]          // ordinary member / empty file surface
    [InlineData(-1, 900)]         // defensive: negative counts clamp to zero
    [InlineData(int.MinValue, 900)]
    [InlineData(1, 902)]
    [InlineData(100, 1100)]       // 百项
    [InlineData(500, 1900)]
    [InlineData(1000, 2900)]      // 千项
    [InlineData(1550, 4000)]      // exact ceiling
    [InlineData(5000, 4000)]      // cap holds far beyond the curve
    [InlineData(int.MaxValue, 4000)] // no overflow
    public void ResolveBudget_FollowsTheClampedLinearCurve(
        int projectedItemCount,
        int expectedMilliseconds)
    {
        Assert.Equal(
            TimeSpan.FromMilliseconds(expectedMilliseconds),
            WidgetGroupFirstFrameBudgetPolicy.ResolveBudget(projectedItemCount));
    }

    [Fact]
    public void ResolveBudget_IsMonotonicAndBounded()
    {
        TimeSpan previous = TimeSpan.Zero;
        foreach (int itemCount in new[]
                 {
                     0, 1, 7, 25, 100, 250, 500, 1000, 1550, 2000, 10_000, 1_000_000
                 })
        {
            TimeSpan budget = WidgetGroupFirstFrameBudgetPolicy.ResolveBudget(itemCount);
            Assert.True(budget >= previous);
            Assert.True(
                budget <= TimeSpan.FromMilliseconds(
                    WidgetGroupFirstFrameBudgetPolicy.CapMilliseconds));
            previous = budget;
        }
    }
}
