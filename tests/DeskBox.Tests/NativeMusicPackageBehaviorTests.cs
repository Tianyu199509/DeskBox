extern alias MusicPkg;
using System.Runtime.InteropServices;
using System.Text.Json;
using PackageEnvironment = MusicPkg::DeskBox.MusicPackage.Services.MusicEnvironmentContext;
using PackageContent = MusicPkg::DeskBox.MusicPackage.Rendering.MusicWidgetContent;
using PackageModel = MusicPkg::DeskBox.MusicPackage.ViewModels.MusicWidgetViewModel;
using PackagePlayback = MusicPkg::DeskBox.MusicPackage.Services.MusicPlaybackState;
using PackageExports = MusicPkg::DeskBox.MusicPackage.Abi.Exports;
using DeskBox.Services;
namespace DeskBox.Tests;

public sealed class NativeMusicPackageBehaviorTests
{
    [Theory]
    [InlineData("Auto", 360, 320)]
    [InlineData("Auto", 180, 180)]
    [InlineData("Cover", 360, 320)]
    [InlineData("Controls", 180, 180)]
    [InlineData("RecordVertical", 320, 480)]
    [InlineData("RecordHorizontal", 480, 220)]
    public void LayoutMatchesBuiltInOracle(string mode, double width, double height)
    {
        Assert.Equal(DeskBox.Controls.WidgetContents.MusicWidgetContent.ShouldUseMinimalLayout(width, height, mode),
            PackageContent.ShouldUseMinimalLayout(width, height, mode));
    }
    [Fact]
    public void SnapshotPreservesSharedSettingsAndEffectiveEnvironment()
    {
        var value = PackageEnvironment.Parse("""{"music":{"useArtworkBackdrop":false,"enableCoverHoverMotion":false,"displayMode":"RecordHorizontal"},"textSize":14,"cornerRadius":6,"locale":"zh-TW","accent":"#FF123456","isDark":true,"allowTextMarqueeAnimations":false,"allowVinylRotationAnimations":false}""");
        Assert.Equal("RecordHorizontal", value.Music.DisplayMode);
        Assert.False(value.Music.UseArtworkBackdrop);
        Assert.False(value.Music.EnableCoverHoverMotion);
        Assert.Equal(14, value.TextSize);
        Assert.Equal(6, value.CornerRadius);
        Assert.Equal((byte)0x34, value.Accent.G);
        Assert.True(value.IsDark);
        Assert.False(value.AllowTextMarqueeAnimations);
        Assert.False(value.AllowVinylRotationAnimations);
    }
    [Fact]
    public void SnapshotNormalizesMissingAndMalformedOptionalEnvironment()
    {
        var value = PackageEnvironment.Parse("""{"music":{"displayMode":"unknown"},"textSize":500,"cornerRadius":-8,"accent":"invalid"}""");
        Assert.Equal("Auto", value.Music.DisplayMode);
        Assert.Equal(SettingsService.MaxTextSize, value.TextSize);
        Assert.Equal(0, value.CornerRadius);
        Assert.True(value.AllowSystemAnimations);
    }
    [Theory]
    [InlineData(0, false, false)]
    [InlineData(0, true, true)]
    [InlineData(1, true, false)]
    [InlineData(2, true, true)]
    [InlineData(3, true, false)]
    public void ProgressTimerRetainsPlaybackSemantics(int state, bool hasInfo, bool expected) =>
        Assert.Equal(expected, PackageModel.ShouldRunProgressTimer((PackagePlayback)state, hasInfo));
    [Fact]
    public void RevealRefreshesAfterHiddenStateOrExpiredSnapshot()
    {
        DateTime now = new(2026, 9, 10, 10, 0, 0, DateTimeKind.Utc);
        Assert.False(PackageModel.ShouldRefreshAfterReveal(now, now.AddSeconds(-10), false));
        Assert.True(PackageModel.ShouldRefreshAfterReveal(now, now.AddSeconds(-10), true));
        Assert.True(PackageModel.ShouldRefreshAfterReveal(now, now.AddSeconds(-31), false));
        Assert.Equal(500, PackageModel.ResolveProgressRefreshIntervalMs(false));
        Assert.Equal(1000, PackageModel.ResolveProgressRefreshIntervalMs(true));
    }
    [Fact]
    public void WireStructuresMatchFrozenHostAbi()
    {
        Assert.Equal(Marshal.SizeOf<DeskBox.Services.Plugins.NativeHostApiV1>(), Marshal.SizeOf<PackageExports.HostApi>());
        Assert.Equal(Marshal.SizeOf<DeskBox.Services.Plugins.NativeWidgetEventV1>(), Marshal.SizeOf<PackageExports.DeskBoxWidgetEventV1>());
        Assert.Equal(64, Marshal.SizeOf<PackageExports.DeskBoxWidgetEventV1>());
        Assert.Equal(Marshal.OffsetOf<DeskBox.Services.Plugins.NativeHostApiV1>("Context"),
            Marshal.OffsetOf<PackageExports.HostApi>("Context"));
    }
    [Fact]
    public void PartialPatchNeverSuppliesUnchangedFields()
    {
        using var doc = JsonDocument.Parse("""{"displayMode":"Cover"}""");
        Assert.True(MusicInstanceMigration.TryParse(doc.RootElement, false, true, out var settings));
        Assert.Equal("Cover", settings.DisplayMode);
        using var bad = JsonDocument.Parse("""{"displayMode":"Cover","unowned":1}""");
        Assert.False(MusicInstanceMigration.TryParse(bad.RootElement, false, true, out _));
        using var wrong = JsonDocument.Parse("""{"useArtworkBackdrop":"false"}""");
        Assert.False(MusicInstanceMigration.TryParse(wrong.RootElement, false, true, out _));
    }
    [Fact]
    public void RuntimeTextXamlHasNoHostCustomTypesAndAllDataBindingsArePreserved()
    {
        string xaml = File.ReadAllText(TestPaths.FromRepository("src/DeskBox.MusicPackage/Rendering/music.xaml"));
        Assert.DoesNotContain("x:Class=", xaml);
        Assert.DoesNotContain("using:DeskBox.", xaml);
        Assert.DoesNotContain("x:Bind", xaml);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(
            xaml,
            "x:Name=\"Inline[A-Za-z]*VolumeSlider\""));
        Assert.Contains("x:Name=\"InlineSystemVolumeSlider\"", xaml);
        Assert.DoesNotContain("InlineSessionVolumeSlider", xaml);
        string bridge = File.ReadAllText(TestPaths.FromRepository("src/DeskBox.MusicPackage/ViewModels/MusicWidgetViewModel.AotBindableProperties.cs"));
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(xaml, @"\{Binding ([A-Za-z][A-Za-z0-9]*)([^}]*)\}"))
        {
            if (m.Groups[2].Value.Contains("ElementName=")) continue;
            Assert.Contains("nameof(" + m.Groups[1].Value + ")", bridge);
        }
    }
}
