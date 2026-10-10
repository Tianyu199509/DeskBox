using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class BeddingLeakConfirmationPolicyTests
{
    private static readonly IntPtr WitnessA = new(0x1001);
    private static readonly IntPtr WitnessB = new(0x2002);

    [Fact]
    public void Observe_FirstObservationOfAWitnessIsNeverConfirmed()
    {
        var (tracking, confirmed) = BeddingLeakConfirmationPolicy.Observe(
            BeddingLeakConfirmationPolicy.LeakTracking.Empty,
            WitnessA,
            requiredConfirmations: 2);

        Assert.False(confirmed);
        Assert.Equal(WitnessA, tracking.LastWitness);
        Assert.Equal(1, tracking.Confirmations);
    }

    [Fact]
    public void Observe_SameWitnessTwiceConfirms()
    {
        var (first, _) = BeddingLeakConfirmationPolicy.Observe(
            BeddingLeakConfirmationPolicy.LeakTracking.Empty,
            WitnessA,
            requiredConfirmations: 2);
        var (second, confirmed) = BeddingLeakConfirmationPolicy.Observe(
            first,
            WitnessA,
            requiredConfirmations: 2);

        Assert.True(confirmed);
        Assert.Equal(2, second.Confirmations);
    }

    [Fact]
    public void Observe_DifferentWitnessRestartsTheCount()
    {
        var (first, _) = BeddingLeakConfirmationPolicy.Observe(
            BeddingLeakConfirmationPolicy.LeakTracking.Empty,
            WitnessA,
            requiredConfirmations: 2);
        var (second, confirmed) = BeddingLeakConfirmationPolicy.Observe(
            first,
            WitnessB,
            requiredConfirmations: 2);

        // A different foreign window below the group is a different pattern;
        // the conservatism count starts over instead of borrowing evidence
        // from the previous witness.
        Assert.False(confirmed);
        Assert.Equal(WitnessB, second.LastWitness);
        Assert.Equal(1, second.Confirmations);
    }

    [Fact]
    public void Observe_ThreeConfirmationsRequiredNeedsThreeConsecutive()
    {
        var tracking = BeddingLeakConfirmationPolicy.LeakTracking.Empty;
        bool confirmed = false;
        for (int observation = 1; observation <= 3; observation++)
        {
            (tracking, confirmed) = BeddingLeakConfirmationPolicy.Observe(
                tracking,
                WitnessA,
                requiredConfirmations: 3);
            Assert.Equal(observation == 3, confirmed);
        }
    }

    [Fact]
    public void Observe_SingleConfirmationRequirementActsImmediately()
    {
        // Event-driven rechecks at known leak points (file-open dispatch,
        // display-topology restore) act on the first verified observation.
        var (_, confirmed) = BeddingLeakConfirmationPolicy.Observe(
            BeddingLeakConfirmationPolicy.LeakTracking.Empty,
            WitnessA,
            requiredConfirmations: 1);

        Assert.True(confirmed);
    }
}
