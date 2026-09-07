using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using DeskBox.Helpers;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class MusicVolumeAotContractTests
{
    [Theory]
    [InlineData(null, true, 0)]
    [InlineData("", true, 0)]
    [InlineData("rust", true, 1)]
    [InlineData(" RUST ", true, 1)]
    [InlineData("csharp", true, 0)]
    [InlineData(null, false, 1)]
    public void MusicVolumeBackendPolicy_PreservesJitDefaultAndForcesRustWithoutDynamicCode(
        string? configuredValue,
        bool isDynamicCodeSupported,
        int expected)
    {
        Assert.Equal(
            (MusicVolumeBackendMode)expected,
            MusicVolumeBackendPolicy.Resolve(configuredValue, isDynamicCodeSupported));
    }

    [Fact]
    public void ManagedMusicVolumeAbiLayout_MatchesFrozenX64Contract()
    {
        Assert.Equal(88, Marshal.SizeOf<ShortcutNativeModule.NativeMusicVolumeRequest>());
        Assert.Equal(104, Marshal.SizeOf<ShortcutNativeModule.NativeMusicVolumeResult>());
        Assert.Equal(16, Marshal.OffsetOf<ShortcutNativeModule.NativeMusicVolumeRequest>(
            nameof(ShortcutNativeModule.NativeMusicVolumeRequest.SourceAppUserModelId)).ToInt32());
        Assert.Equal(32, Marshal.OffsetOf<ShortcutNativeModule.NativeMusicVolumeRequest>(
            nameof(ShortcutNativeModule.NativeMusicVolumeRequest.SourceDisplayName)).ToInt32());
        Assert.Equal(48, Marshal.OffsetOf<ShortcutNativeModule.NativeMusicVolumeRequest>(
            nameof(ShortcutNativeModule.NativeMusicVolumeRequest.Volume)).ToInt32());
        Assert.Equal(56, Marshal.OffsetOf<ShortcutNativeModule.NativeMusicVolumeRequest>(
            nameof(ShortcutNativeModule.NativeMusicVolumeRequest.Reserved1)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<ShortcutNativeModule.NativeMusicVolumeResult>(
            nameof(ShortcutNativeModule.NativeMusicVolumeResult.AttemptedPhases)).ToInt32());
        Assert.Equal(24, Marshal.OffsetOf<ShortcutNativeModule.NativeMusicVolumeResult>(
            nameof(ShortcutNativeModule.NativeMusicVolumeResult.ComHResult)).ToInt32());
        Assert.Equal(44, Marshal.OffsetOf<ShortcutNativeModule.NativeMusicVolumeResult>(
            nameof(ShortcutNativeModule.NativeMusicVolumeResult.HasSessionVolume)).ToInt32());
        Assert.Equal(56, Marshal.OffsetOf<ShortcutNativeModule.NativeMusicVolumeResult>(
            nameof(ShortcutNativeModule.NativeMusicVolumeResult.SystemVolume)).ToInt32());
        Assert.Equal(72, Marshal.OffsetOf<ShortcutNativeModule.NativeMusicVolumeResult>(
            nameof(ShortcutNativeModule.NativeMusicVolumeResult.Reserved1)).ToInt32());
        Assert.Equal(1UL << 5, ShortcutNativeModule.MusicVolumeCapability);
    }

    [Fact]
    public void RustMusicVolume_ReadOnlyProbeCrossesTheRealAbiBoundary()
    {
        MusicVolumeNativeCallResult result = MusicVolumeNativeBackend.GetSnapshot(
            "DeskBox.Contract.Probe",
            "DeskBox Contract Probe");

        Assert.DoesNotContain(
            result.Failure,
            new[]
            {
                MusicVolumeNativeCallFailure.ModuleUnavailable,
                MusicVolumeNativeCallFailure.CapabilityUnavailable,
                MusicVolumeNativeCallFailure.MissingExport,
                MusicVolumeNativeCallFailure.InvalidInput,
                MusicVolumeNativeCallFailure.InvalidNativeResult
            });
        if (result.Success)
        {
            Assert.InRange(result.SystemVolume, 0.0, 1.0);
            Assert.InRange(result.SessionVolume, 0.0, 1.0);
        }
    }

    [Fact]
    public async Task MusicVolumeService_ExplicitRustModeUsesTheProductCallPath()
    {
        var service = new MusicVolumeService(MusicVolumeBackendMode.Rust);

        MusicVolumeSnapshot snapshot = await service.GetVolumeAsync(
            "DeskBox.Contract.Probe",
            "DeskBox Contract Probe");

        Assert.InRange(snapshot.SystemVolume, 0.0, 1.0);
        Assert.InRange(snapshot.SessionVolume, 0.0, 1.0);
    }

    [Fact]
    public void MusicVolumeService_UsesRustForAotAndCompileTimeExcludesLegacyCom()
    {
        string source = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Services/MusicVolumeService.cs"));

        Assert.Contains("MusicVolumeBackendPolicy.Current", source, StringComparison.Ordinal);
        Assert.Contains("MusicVolumeNativeBackend", source, StringComparison.Ordinal);
        Assert.Contains("#if !DESKBOX_NATIVE_AOT", source, StringComparison.Ordinal);
        var legacyComBlock = new Regex(
            @"#if !DESKBOX_NATIVE_AOT\s+private static double GetSystemMasterVolume\(\)[\s\S]*?private interface ISimpleAudioVolume[\s\S]*?#endif",
            RegexOptions.CultureInvariant);
        Assert.Matches(legacyComBlock, source);
        Assert.DoesNotContain(
            "[ComImport]",
            legacyComBlock.Replace(source, string.Empty),
            StringComparison.Ordinal);
    }

    [Fact]
    public void NativeAbi_ExposesVersionedMusicVolumeBoundary()
    {
        string crate = File.ReadAllText(
            TestPaths.FromRepository("native/deskbox-native/Cargo.toml"));
        string source = File.ReadAllText(
            TestPaths.FromRepository("native/deskbox-native/src/lib.rs"));
        string musicSource = File.ReadAllText(
            TestPaths.FromRepository("native/deskbox-native/src/music_volume.rs"));
        string header = File.ReadAllText(
            TestPaths.FromRepository("native/include/deskbox_native.h"));
        string buildScript = File.ReadAllText(
            TestPaths.FromRepository("scripts/build-rust-native.ps1"));

        Assert.Contains("\"Win32_Media_Audio\"", crate, StringComparison.Ordinal);
        Assert.Contains("\"Win32_Media_Audio_Endpoints\"", crate, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_NATIVE_CAPABILITY_MUSIC_VOLUME_V1", source, StringComparison.Ordinal);
        Assert.Contains("pub struct DeskBoxMusicVolumeRequestV1", source, StringComparison.Ordinal);
        Assert.Contains("pub struct DeskBoxMusicVolumeResultV1", source, StringComparison.Ordinal);
        Assert.Contains("pub unsafe extern \"C\" fn deskbox_music_volume_v1(", source, StringComparison.Ordinal);
        Assert.Contains("music_volume::execute", source, StringComparison.Ordinal);
        Assert.Contains("CoInitializeEx", musicSource, StringComparison.Ordinal);
        Assert.Contains("RPC_E_CHANGED_MODE", musicSource, StringComparison.Ordinal);
        Assert.Contains("GetDefaultAudioEndpoint", musicSource, StringComparison.Ordinal);
        Assert.Contains("GetSessionEnumerator", musicSource, StringComparison.Ordinal);
        Assert.Contains("typedef struct DeskBoxMusicVolumeRequestV1", header, StringComparison.Ordinal);
        Assert.Contains("typedef struct DeskBoxMusicVolumeResultV1", header, StringComparison.Ordinal);
        Assert.Contains("deskbox_music_volume_v1(", header, StringComparison.Ordinal);
        Assert.Contains("deskbox_music_volume_v1", buildScript, StringComparison.Ordinal);
        Assert.Contains("capability mismatch: expected 511", buildScript, StringComparison.Ordinal);
    }

    [Fact]
    public void AotAudit_RequiresZeroMusicVolumeAlwaysThrowMessages()
    {
        string script = File.ReadAllText(
            TestPaths.FromRepository("scripts/publish-aot-audit.ps1"));

        Assert.Contains("auditProfileVersion = 61", script, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 55", script, StringComparison.Ordinal);
        Assert.Contains("musicVolumeAlwaysThrowMessages", script, StringComparison.Ordinal);
        Assert.Contains("musicVolumeBackendPolicy", script, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\"DeskBox.Services.MusicVolumeService+MMDeviceEnumeratorComObject\"",
            script,
            StringComparison.Ordinal);
    }
}
