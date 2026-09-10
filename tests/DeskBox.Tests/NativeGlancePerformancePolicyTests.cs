extern alias GlancePkg;

using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.Services.Plugins;
using HostConfig = GlancePkg::DeskBox.GlancePackage.Services.HostConfig;
using PackagePerformancePolicy = GlancePkg::DeskBox.GlancePackage.Services.PackagePerformancePolicy;

namespace DeskBox.Tests;

// HostConfig stores the package's process-wide native callback.
[CollectionDefinition("NativeGlanceHostEnvironment", DisableParallelization = true)]
public sealed class NativeGlanceHostEnvironmentCollection { }

[Collection("NativeGlanceHostEnvironment")]
public sealed class NativeGlancePerformancePolicyTests
{
    [Theory]
    [InlineData("Balanced")]
    [InlineData("ResourceSaver")]
    [InlineData("Custom")]
    [InlineData("resourcesaver")]
    [InlineData("custom")]
    [InlineData("BestVisual")]
    [InlineData("future-mode")]
    [InlineData(null)]
    public void HostResolvedModesAndSwitchCombinationsReachActualPackageParser(string? mode)
    {
        for (int switches = 0; switches < 16; switches++)
        foreach (bool legacySwitch in new[] { false, true })
        foreach (bool allowAnimations in new[] { false, true })
        {
            var settings = new AppSettings
            {
                PerformanceMode = mode!,
                EnableTextMarqueeAnimations = (switches & 1) != 0,
                EnableVinylRotationAnimations = (switches & 2) != 0,
                EnableGlanceImageAutoRotation = (switches & 4) != 0,
                EnableCompactAmbientAnimations = (switches & 8) != 0,
                EnableContinuousDecorativeAnimations = legacySwitch,
            };
            string json = NativeHostApiBridge.BuildConfigJson("zh-CN", "#0078D4",
                performanceSettings: settings, allowDecorativeAnimations: allowAnimations);

            using var document = JsonDocument.Parse(json);
            Assert.Equal(allowAnimations ? JsonValueKind.True : JsonValueKind.False,
                document.RootElement.GetProperty("allowDecorativeAnimations").ValueKind);
            Assert.Equal(settings.EnableGlanceImageAutoRotation ? JsonValueKind.True : JsonValueKind.False,
                document.RootElement.GetProperty("allowImageAutoRotation").ValueKind);

            var actual = PackagePerformancePolicy.Parse(json);
            Assert.Equal(PerformanceSettingsPolicy.Resolve(settings).AllowGlanceImageAutoRotation,
                actual.AllowImageAutoRotation);
            // Presets and the legacy aggregate do not disable an individually
            // enabled Glance rotation; other widgets' switches cannot enable it.
            Assert.Equal(settings.EnableGlanceImageAutoRotation, actual.AllowImageAutoRotation);
            Assert.Equal(allowAnimations, actual.AllowDecorativeAnimations);
            Assert.Equal(mode, settings.PerformanceMode);
            Assert.Equal(legacySwitch, settings.EnableContinuousDecorativeAnimations);
        }
    }

    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(false, true, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, true, false)]
    [InlineData(false, false, true, false)]
    public void TransitionsFollowHostAccessibilityResolverIndependentlyOfRotation(
        bool animations, bool advancedEffects, bool highContrast, bool expected)
    {
        bool resolved = WindowsCompatibilityService.ResolveShouldAnimate(animations, advancedEffects, highContrast);
        string json = NativeHostApiBridge.BuildConfigJson("en-US", "#0078D4",
            performanceSettings: new AppSettings { EnableGlanceImageAutoRotation = false },
            allowDecorativeAnimations: resolved);
        var actual = PackagePerformancePolicy.Parse(json);
        Assert.Equal(expected, actual.AllowDecorativeAnimations);
        Assert.False(actual.AllowImageAutoRotation);
    }

    [Fact]
    public void ExistingWriterCallsAndPackageDefaultsMatchBuiltInDefaults()
    {
        var defaults = PackagePerformancePolicy.Default;
        Assert.True(defaults.AllowDecorativeAnimations);
        Assert.Equal(PerformanceSettingsPolicy.Resolve(new AppSettings()).AllowGlanceImageAutoRotation,
            defaults.AllowImageAutoRotation);
        Assert.Equal(defaults, PackagePerformancePolicy.Parse(
            NativeHostApiBridge.BuildConfigJson("zh-CN", "#0078D4")));
        Assert.Equal(defaults, PackagePerformancePolicy.Parse(
            NativeHostApiBridge.BuildConfigJson("zh-CN", "#0078D4", "Light", "Mica", 0.8, 0.65)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("{broken")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("false")]
    [InlineData("{}")]
    [InlineData("""{"locale":"zh-CN","theme":"Light"}""")]
    [InlineData("""{"allowImageAutoRotation":"false","allowDecorativeAnimations":0}""")]
    [InlineData("""{"allowImageAutoRotation":null,"allowDecorativeAnimations":[]}""")]
    public void MissingOrInvalidEnvironmentKeepsCompatibleDefaults(string? json) =>
        Assert.Equal(PackagePerformancePolicy.Default, PackagePerformancePolicy.Parse(json));

    [Theory]
    [InlineData("""{"allowImageAutoRotation":false}""", false, true)]
    [InlineData("""{"allowDecorativeAnimations":false}""", true, false)]
    [InlineData("""{"allowImageAutoRotation":false,"allowDecorativeAnimations":"false"}""", false, true)]
    [InlineData("""{"allowImageAutoRotation":{},"allowDecorativeAnimations":false}""", true, false)]
    [InlineData("""{"allowImageAutoRotation":false,"allowDecorativeAnimations":false,"future":42}""", false, false)]
    public void InvalidFieldsFallBackIndependentlyAndUnknownFieldsAreIgnored(
        string json, bool rotation, bool animations) =>
        Assert.Equal(new PackagePerformancePolicy(rotation, animations), PackagePerformancePolicy.Parse(json));

    [Fact]
    public void HostConfigReadsFreshSnapshotsThroughExistingTwoCallChannelAndResets()
    {
        var settings = new AppSettings();
        string payload = NativeHostApiBridge.BuildConfigJson("zh-CN", "#0078D4", performanceSettings: settings);
        int reads = 0;
        GetConfigJson callback = (buffer, length) =>
        {
            reads++;
            byte[] bytes = Encoding.UTF8.GetBytes(payload);
            if (length < bytes.Length) return bytes.Length;
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            return bytes.Length;
        };

        HostConfig.Reset();
        try
        {
            Assert.Equal(PackagePerformancePolicy.Default, HostConfig.ReadPerformancePolicy());
            HostConfig.Initialize(Marshal.GetFunctionPointerForDelegate(callback));
            var first = HostConfig.ReadPerformancePolicy();
            Assert.Equal(PackagePerformancePolicy.Default, first);
            Assert.Equal(2, reads);

            settings.EnableGlanceImageAutoRotation = false;
            payload = NativeHostApiBridge.BuildConfigJson("zh-CN", "#0078D4",
                performanceSettings: settings, allowDecorativeAnimations: false);
            Assert.Equal(new PackagePerformancePolicy(false, false), HostConfig.ReadPerformancePolicy());
            Assert.Equal(4, reads);
            Assert.Equal(PackagePerformancePolicy.Default, first);

            payload = "{broken";
            Assert.Equal(PackagePerformancePolicy.Default, HostConfig.ReadPerformancePolicy());
            HostConfig.Reset();
            Assert.Equal(PackagePerformancePolicy.Default, HostConfig.ReadPerformancePolicy());
            Assert.Equal(6, reads);
        }
        finally
        {
            HostConfig.Reset();
            GC.KeepAlive(callback);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetConfigJson(nint buffer, int bufferLength);
}
