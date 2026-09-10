using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
namespace DeskBox.MusicPackage.Services;

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

internal sealed unsafe partial class RustMusicVolumeModule
{
    private const int HResultNotAttempted = unchecked((int)0x8000000A);
    private const uint StatusOk = 0;
    private readonly nint _module;
    internal ulong Capabilities { get; }
    internal string ModulePath { get; }
    private RustMusicVolumeModule(nint module, string path, ulong capabilities)
    { _module = module; ModulePath = path; Capabilities = capabilities; }

    internal static RustMusicVolumeModule? Load(out string detail)
    {
        detail = "";
        // The host already ships this ABI for package signatures and OS services.
        // Never resolve from CWD or a package-controlled DLL search path.
        string path = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath!)!, "deskbox_native.dll");
        nint module = LoadLibraryEx(path, 0, 0x00000100 | 0x00000800);
        if (module == 0) { detail = $"Native music backend unavailable at {path}: {Marshal.GetLastPInvokeError()}"; return null; }
        bool keep = false;
        try
        {
            if (!NativeLibrary.TryGetExport(module, "deskbox_native_abi_version", out nint abi) ||
                !NativeLibrary.TryGetExport(module, "deskbox_native_capabilities", out nint caps) ||
                !NativeLibrary.TryGetExport(module, "deskbox_music_volume_v1", out _))
            { detail = "Required native music exports are missing."; return null; }
            uint version = ((delegate* unmanaged[Cdecl]<uint>)abi)();
            ulong mask = ((delegate* unmanaged[Cdecl]<ulong>)caps)();
            if (version != 2 || (mask & MusicVolumeCapability) == 0)
            { detail = $"Native music ABI/capability mismatch: {version}/{mask}."; return null; }
            keep = true;
            return new(module, path, mask);
        }
        finally { if (!keep) NativeLibrary.Free(module); }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint LoadLibraryEx(string path, nint file, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct NativeUtf16String
    {
        internal NativeUtf16String(char* data, int length) { Data = length == 0 ? null : data; LengthChars = (uint)length; Reserved0 = 0; }
        internal readonly char* Data;
        internal readonly uint LengthChars;
        internal readonly uint Reserved0;
    }

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


