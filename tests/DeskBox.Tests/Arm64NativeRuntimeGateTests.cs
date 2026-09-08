using System.Runtime.InteropServices;
using DeskBox.Helpers;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class Arm64NativeRuntimeGateTests
{
    private const string RequiredEnvironmentVariable =
        "DESKBOX_REQUIRE_ARM64_RUNTIME_GATE";

    [Fact]
    public void OptInGate_LoadsArm64ProductModuleAndExecutesAbiProbe()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(RequiredEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        Assert.Equal(Architecture.Arm64, RuntimeInformation.OSArchitecture);
        Assert.Equal(Architecture.Arm64, RuntimeInformation.ProcessArchitecture);

        string nativePath = Path.Combine(
            AppContext.BaseDirectory,
            ShortcutNativeModule.DllName);
        ShortcutNativeLoadResult native = ShortcutNativeModule.Load(nativePath);
        Assert.True(native.Success, $"{native.Failure}: {native.Detail}");
        Assert.NotNull(native.Module);
        Assert.Equal(2U, native.Module.ProbeAbiVersion());
        Assert.Equal(1023UL, native.Module.ProbeCapabilities());

    }
}
