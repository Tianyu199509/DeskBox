extern alias WeatherPkg;
using System.Runtime.InteropServices;
using DeskBox.Services.Plugins;
using W = WeatherPkg::DeskBox.WeatherPackage.Abi.Exports;
using P = WeatherPkg::DeskBox.WeatherPackage.ViewModels.WeatherWidgetViewModel;

namespace DeskBox.Tests;

public sealed class NativeWeatherBoundaryTests
{
    [Fact]
    public void NativeAbiPayloadAndHostTableKeepFrozenLayout()
    {
        Assert.Equal(Marshal.SizeOf<NativeHostApiV1>(), Marshal.SizeOf<W.HostApi>());
        Assert.Equal(Marshal.SizeOf<NativeWidgetEventV1>(), Marshal.SizeOf<W.DeskBoxWidgetEventV1>());
        foreach (string field in new[] { "Size", "Version", "Kind", "Flags", "Width", "Height", "Reserved0", "Reserved3" })
            Assert.Equal(Marshal.OffsetOf<NativeWidgetEventV1>(field), Marshal.OffsetOf<W.DeskBoxWidgetEventV1>(field));
        Assert.Equal((uint)WidgetLifecycleEventKind.ResponsiveLayoutCancel, W.ResponsiveLayoutCancel);
        Assert.Equal((uint)WidgetLifecycleEventKind.VisibilityChanged, W.VisibilityChanged);
    }

    [Theory]
    [InlineData(144, 100, "Expanded", 11.5, 1)]
    [InlineData(210, 160, "Mini", 11.5, 1)]
    [InlineData(310, 280, "Compact", 11.5, 1)]
    [InlineData(310, 280, "Expanded", 16, 2)]
    public void ResponsiveBreakpointsMatchExistingWeather(double width, double height, string previous, double textSize, double systemScale)
    {
        Assert.Equal(DeskBox.ViewModels.WeatherWidgetViewModel.DetermineLayoutMode(width, height, previous, textSize, systemScale),
            P.DetermineLayoutMode(width, height, previous, textSize, systemScale));
    }

    [Fact]
    public void PackageDoesNotReferenceHostAssemblyOrReadHostFilesThroughLinks()
    {
        string project = File.ReadAllText(TestPaths.FromRepository("src/DeskBox.WeatherPackage/DeskBox.WeatherPackage.csproj"));
        Assert.DoesNotContain("ProjectReference", project);
        Assert.DoesNotContain("Compile Include", project);
        string xaml = File.ReadAllText(TestPaths.FromRepository("src/DeskBox.WeatherPackage/Rendering/weather.xaml"));
        Assert.DoesNotContain("x:Class=", xaml);
        Assert.DoesNotContain("x:Bind", xaml);
        Assert.DoesNotContain("Converter=", xaml);
    }
}
