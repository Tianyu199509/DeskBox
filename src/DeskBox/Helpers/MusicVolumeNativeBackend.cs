using System.Runtime.InteropServices;

namespace DeskBox.Helpers;

internal enum MusicVolumeBackendMode
{
    Rust
}

internal static class MusicVolumeBackendPolicy
{
    internal static MusicVolumeBackendMode Current => MusicVolumeBackendMode.Rust;
}

internal enum MusicVolumeNativeCallFailure
{
    None,
    ModuleUnavailable,
    CapabilityUnavailable,
    MissingExport,
    InvalidInput,
    NativeFailure,
    InvalidNativeResult
}

internal sealed record MusicVolumeNativeCallResult(
    MusicVolumeNativeCallFailure Failure,
    string Detail,
    uint Status,
    int OperationHResult,
    uint AttemptedPhases,
    uint MatchKind,
    int ComHResult,
    int CreateHResult,
    int DeviceHResult,
    int SystemHResult,
    int SessionHResult,
    double SystemVolume,
    double SessionVolume,
    bool HasSessionVolume)
{
    internal bool Success => Failure == MusicVolumeNativeCallFailure.None;
}

internal sealed unsafe partial class ShortcutNativeModule
{
    internal const ulong MusicVolumeCapability = 1UL << 5;

    private const uint MusicVolumeStructVersion = 1;
    private const uint MusicVolumeOperationGetSnapshot = 1;
    private const uint MusicVolumeOperationGetSystem = 2;
    private const uint MusicVolumeOperationSetSystem = 3;
    private const uint MusicVolumeOperationSetSession = 4;
    private const uint MusicVolumeAttemptedPhasesMask = (1U << 6) - 1;
    private const uint MusicVolumeMaximumMatchKind = 7;
    private const int MusicVolumeMaxInputChars = 32_767;
    private const string MusicVolumeExport = "deskbox_music_volume_v1";

    internal MusicVolumeNativeCallResult GetMusicVolumeSnapshot(
        string sourceAppUserModelId,
        string sourceDisplayName)
    {
        return InvokeMusicVolume(
            MusicVolumeOperationGetSnapshot,
            sourceAppUserModelId,
            sourceDisplayName,
            0.0);
    }

    internal MusicVolumeNativeCallResult GetSystemMusicVolume()
    {
        return InvokeMusicVolume(MusicVolumeOperationGetSystem, string.Empty, string.Empty, 0.0);
    }

    internal MusicVolumeNativeCallResult SetSystemMusicVolume(double volume)
    {
        return InvokeMusicVolume(MusicVolumeOperationSetSystem, string.Empty, string.Empty, volume);
    }

    internal MusicVolumeNativeCallResult SetSessionMusicVolume(
        string sourceAppUserModelId,
        string sourceDisplayName,
        double volume)
    {
        return InvokeMusicVolume(
            MusicVolumeOperationSetSession,
            sourceAppUserModelId,
            sourceDisplayName,
            volume);
    }

    private MusicVolumeNativeCallResult InvokeMusicVolume(
        uint operation,
        string sourceAppUserModelId,
        string sourceDisplayName,
        double volume)
    {
        if ((Capabilities & MusicVolumeCapability) == 0)
        {
            return MusicVolumeCallFailure(
                MusicVolumeNativeCallFailure.CapabilityUnavailable,
                $"Native music-volume capability 0x{MusicVolumeCapability:X} is unavailable; module mask is 0x{Capabilities:X}.");
        }

        if (!IsValidMusicVolumeInput(sourceAppUserModelId) ||
            !IsValidMusicVolumeInput(sourceDisplayName))
        {
            return MusicVolumeCallFailure(
                MusicVolumeNativeCallFailure.InvalidInput,
                "Music-volume identity is too long or contains an embedded NUL.");
        }

        if (!NativeLibrary.TryGetExport(_module, MusicVolumeExport, out nint exportAddress))
        {
            return MusicVolumeCallFailure(
                MusicVolumeNativeCallFailure.MissingExport,
                $"The DeskBox native export '{MusicVolumeExport}' is missing.");
        }

        var operationExport =
            (delegate* unmanaged[Cdecl]<NativeMusicVolumeRequest*, NativeMusicVolumeResult*, uint>)
            (void*)exportAddress;
        fixed (char* sourceAppPointer = sourceAppUserModelId)
        fixed (char* sourceDisplayNamePointer = sourceDisplayName)
        {
            var request = new NativeMusicVolumeRequest
            {
                StructSize = (uint)sizeof(NativeMusicVolumeRequest),
                StructVersion = MusicVolumeStructVersion,
                Operation = operation,
                SourceAppUserModelId = new NativeUtf16String(
                    sourceAppPointer,
                    sourceAppUserModelId.Length),
                SourceDisplayName = new NativeUtf16String(
                    sourceDisplayNamePointer,
                    sourceDisplayName.Length),
                Volume = volume
            };
            var result = new NativeMusicVolumeResult
            {
                StructSize = (uint)sizeof(NativeMusicVolumeResult),
                StructVersion = MusicVolumeStructVersion
            };

            uint returnedStatus = operationExport(&request, &result);
            if (result.StructSize != (uint)sizeof(NativeMusicVolumeResult) ||
                result.StructVersion != MusicVolumeStructVersion)
            {
                return FromMusicVolumeResult(
                    MusicVolumeNativeCallFailure.InvalidNativeResult,
                    $"Native music-volume result envelope mismatch: size={result.StructSize}, version={result.StructVersion}.",
                    result);
            }

            if (returnedStatus != result.Status ||
                (result.AttemptedPhases & ~MusicVolumeAttemptedPhasesMask) != 0 ||
                result.MatchKind > MusicVolumeMaximumMatchKind ||
                result.HasSessionVolume > 1 ||
                result.OperationSucceeded > 1 ||
                result.Reserved0 != 0 ||
                result.Reserved1 != 0 ||
                result.Reserved2 != 0 ||
                result.Reserved3 != 0 ||
                result.Reserved4 != 0)
            {
                return FromMusicVolumeResult(
                    MusicVolumeNativeCallFailure.InvalidNativeResult,
                    $"Native music-volume result is inconsistent: return={returnedStatus}, result={result.Status}.",
                    result);
            }

            if (result.Status != StatusOk || result.OperationSucceeded != 1)
            {
                return FromMusicVolumeResult(
                    MusicVolumeNativeCallFailure.NativeFailure,
                    $"Native music-volume operation failed: status={result.Status}, HRESULT=0x{result.OperationHResult:X8}.",
                    result);
            }

            if (!double.IsFinite(result.SystemVolume) ||
                !double.IsFinite(result.SessionVolume) ||
                result.SystemVolume is < 0.0 or > 1.0 ||
                result.SessionVolume is < 0.0 or > 1.0)
            {
                return FromMusicVolumeResult(
                    MusicVolumeNativeCallFailure.InvalidNativeResult,
                    "Native music-volume result contains an invalid normalized volume.",
                    result);
            }

            return FromMusicVolumeResult(MusicVolumeNativeCallFailure.None, string.Empty, result);
        }
    }

    private static bool IsValidMusicVolumeInput(string? value)
    {
        return value is not null &&
               value.Length <= MusicVolumeMaxInputChars &&
               !value.Contains('\0');
    }

    private static MusicVolumeNativeCallResult MusicVolumeCallFailure(
        MusicVolumeNativeCallFailure failure,
        string detail)
    {
        return new MusicVolumeNativeCallResult(
            failure,
            detail,
            0,
            HResultNotAttempted,
            0,
            0,
            HResultNotAttempted,
            HResultNotAttempted,
            HResultNotAttempted,
            HResultNotAttempted,
            HResultNotAttempted,
            0.0,
            0.0,
            false);
    }

    private static MusicVolumeNativeCallResult FromMusicVolumeResult(
        MusicVolumeNativeCallFailure failure,
        string detail,
        NativeMusicVolumeResult result)
    {
        return new MusicVolumeNativeCallResult(
            failure,
            detail,
            result.Status,
            result.OperationHResult,
            result.AttemptedPhases,
            result.MatchKind,
            result.ComHResult,
            result.CreateHResult,
            result.DeviceHResult,
            result.SystemHResult,
            result.SessionHResult,
            result.SystemVolume,
            result.SessionVolume,
            result.HasSessionVolume == 1);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeMusicVolumeRequest
    {
        internal uint StructSize;
        internal uint StructVersion;
        internal uint Operation;
        internal uint Flags;
        internal NativeUtf16String SourceAppUserModelId;
        internal NativeUtf16String SourceDisplayName;
        internal double Volume;
        internal ulong Reserved1;
        internal ulong Reserved2;
        internal ulong Reserved3;
        internal ulong Reserved4;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeMusicVolumeResult
    {
        internal uint StructSize;
        internal uint StructVersion;
        internal uint Status;
        internal int OperationHResult;
        internal uint AttemptedPhases;
        internal uint MatchKind;
        internal int ComHResult;
        internal int CreateHResult;
        internal int DeviceHResult;
        internal int SystemHResult;
        internal int SessionHResult;
        internal uint HasSessionVolume;
        internal uint OperationSucceeded;
        internal uint Reserved0;
        internal double SystemVolume;
        internal double SessionVolume;
        internal ulong Reserved1;
        internal ulong Reserved2;
        internal ulong Reserved3;
        internal ulong Reserved4;
    }
}

internal static class MusicVolumeNativeBackend
{
    internal static MusicVolumeNativeCallResult GetSnapshot(
        string sourceAppUserModelId,
        string sourceDisplayName)
    {
        return Invoke(module => module.GetMusicVolumeSnapshot(
            sourceAppUserModelId,
            sourceDisplayName));
    }

    internal static MusicVolumeNativeCallResult GetSystemVolume()
    {
        return Invoke(static module => module.GetSystemMusicVolume());
    }

    internal static MusicVolumeNativeCallResult SetSystemVolume(double volume)
    {
        return Invoke(module => module.SetSystemMusicVolume(volume));
    }

    internal static MusicVolumeNativeCallResult SetSessionVolume(
        string sourceAppUserModelId,
        string sourceDisplayName,
        double volume)
    {
        return Invoke(module => module.SetSessionMusicVolume(
            sourceAppUserModelId,
            sourceDisplayName,
            volume));
    }

    private static MusicVolumeNativeCallResult Invoke(
        Func<ShortcutNativeModule, MusicVolumeNativeCallResult> operation)
    {
        ShortcutNativeLoadResult load = ShortcutNativeModule.Default;
        if (!load.Success)
        {
            return new MusicVolumeNativeCallResult(
                MusicVolumeNativeCallFailure.ModuleUnavailable,
                $"{load.Failure}: {load.Detail}",
                0,
                ShortcutNativeModule.HResultNotAttempted,
                0,
                0,
                ShortcutNativeModule.HResultNotAttempted,
                ShortcutNativeModule.HResultNotAttempted,
                ShortcutNativeModule.HResultNotAttempted,
                ShortcutNativeModule.HResultNotAttempted,
                ShortcutNativeModule.HResultNotAttempted,
                0.0,
                0.0,
                false);
        }

        return operation(load.Module!);
    }
}
