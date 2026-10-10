using DeskBox.Services;

namespace DeskBox.Tests;

/// <summary>
/// Counting semantics for display identity tokens: degenerate monitors of
/// the same resolution collapse onto one geo token, so subset comparisons
/// must compare occurrences, not set membership.
/// </summary>
public sealed class DisplayIdentityTokensTests
{
    [Fact]
    public void IsTrueSubset_SingleDuplicateUnit_IsTrueSubsetOfThePair()
    {
        Assert.True(DisplayIdentityTokens.IsTrueSubset(
            ["geo:1920x1080"],
            ["geo:1920x1080", "geo:1920x1080"]));
    }

    [Fact]
    public void IsTrueSubset_EqualDuplicateCounts_IsNotTrueSubset()
    {
        Assert.False(DisplayIdentityTokens.IsTrueSubset(
            ["geo:1920x1080", "geo:1920x1080"],
            ["geo:1920x1080", "geo:1920x1080"]));
    }

    [Fact]
    public void IsTrueSubset_UnknownToken_DisqualifiesEvenAgainstDuplicates()
    {
        Assert.False(DisplayIdentityTokens.IsTrueSubset(
            ["geo:1920x1080", "OTHER"],
            ["geo:1920x1080", "geo:1920x1080"]));
    }
}
