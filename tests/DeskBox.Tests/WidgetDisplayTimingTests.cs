using System.Runtime.InteropServices;
using DeskBox.Helpers;
using DeskBox.Models;

namespace DeskBox.Tests;

public sealed class WidgetDisplayTimingTests
{
    [Theory]
    [InlineData(60000u, 1001u, 60, 59.94005994)]
    [InlineData(144000u, 1000u, 60, 144)]
    [InlineData(0u, 0u, 120, 120)]
    [InlineData(1u, 1u, 60, 60)]
    [InlineData(240000u, 1001u, 60, 239.76023976)]
    public void RationalDisplayModes_PreservePrecisionAndRejectDriverSentinels(
        uint numerator, uint denominator, double fallback, double expected)
    {
        Assert.Equal(expected, WidgetDisplayRefreshRatePolicy.ResolveRationalRate(numerator, denominator, fallback), 6);
    }

    [Fact]
    public void DisplayConfigInterop_MatchesNativeUnionAndPathLayout()
    {
        // The same native ABI is used on x64 and ARM64. A union offset mistake
        // silently reads horizontal frequency or a source mode as the refresh rate.
        Assert.Equal(20, Marshal.SizeOf<Win32Helper.DisplayPathSource>());
        Assert.Equal(48, Marshal.SizeOf<Win32Helper.DisplayPathTarget>());
        Assert.Equal(72, Marshal.SizeOf<Win32Helper.DisplayPath>());
        Assert.Equal(64, Marshal.SizeOf<Win32Helper.DisplayMode>());
        Assert.Equal(32, Marshal.OffsetOf<Win32Helper.DisplayMode>(nameof(Win32Helper.DisplayMode.VerticalSync)).ToInt32());
    }
}
