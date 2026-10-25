using DeskBox.Contracts;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class FrostedGlassMaterialTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(0.5, 16)]
    [InlineData(1, 32)]
    public void StrengthControlsRealBlurRadius(double strength, float expectedRadius) =>
        Assert.Equal(expectedRadius, FrostedGlassBlur.Radius(strength));

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(2, 32)]
    [InlineData(double.NaN, 11.2f)]
    [InlineData(double.PositiveInfinity, 11.2f)]
    public void InvalidStrengthNeverReachesTheCompositor(double strength, float expectedRadius) =>
        Assert.Equal(expectedRadius, FrostedGlassBlur.Radius(strength));

    [Fact]
    public void ReducingStrengthAlwaysMakesTheBackdropClearer()
    {
        float previous = FrostedGlassBlur.Radius(0);
        for (int percent = 2; percent <= 100; percent += 2)
        {
            float current = FrostedGlassBlur.Radius(percent / 100d);
            Assert.True(current > previous);
            previous = current;
        }
    }

    [Fact]
    public void GlassHasOneStrengthControlAndNoOpacityControl()
    {
        Assert.False(WidgetMaterialKinds.SupportsOpacity(WidgetMaterialKinds.Glass));
        Assert.True(WidgetMaterialKinds.SupportsMaterialIntensity(WidgetMaterialKinds.Glass));
        Assert.Equal(WidgetMaterialKinds.Glass,
            WindowsCompatibilityService.ResolveWidgetMaterialTypeForBuild(WidgetMaterialKinds.Glass, 22621));
        Assert.Equal(WidgetMaterialKinds.Acrylic,
            WindowsCompatibilityService.ResolveWidgetMaterialTypeForBuild(WidgetMaterialKinds.Glass, 19045));
    }
}