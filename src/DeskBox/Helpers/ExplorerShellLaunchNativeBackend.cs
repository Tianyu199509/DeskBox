using System.Runtime.InteropServices;

namespace DeskBox.Helpers;

internal enum ExplorerShellLaunchBackendMode
{
    Rust
}

internal static class ExplorerShellLaunchBackendPolicy
{
    internal static ExplorerShellLaunchBackendMode Current => ExplorerShellLaunchBackendMode.Rust;
}

internal enum ExplorerShellLaunchNativeCallFailure
{
    None,
    ModuleUnavailable,
    CapabilityUnavailable,
    MissingExport,
    InvalidInput,
    NativeFailure,
    InvalidNativeResult
}

internal sealed record ExplorerShellLaunchNativeCallResult(
    ExplorerShellLaunchNativeCallFailure Failure,
    string Detail,
    uint Status,
    int OperationHResult,
    uint AttemptedPhases,
    int ComHResult,
    int CreateHResult,
    int WindowsHResult,
    int DesktopHResult,
    int DocumentHResult,
    int ApplicationHResult,
    int ExecuteHResult)
{
    internal bool Success => Failure == ExplorerShellLaunchNativeCallFailure.None;
}

internal sealed unsafe partial class ShortcutNativeModule
{
    internal const ulong ExplorerShellLaunchCapability = 1UL << 6;

    private const uint ExplorerShellLaunchStructVersion = 1;
    private const uint ExplorerShellLaunchAttemptedPhasesMask = (1U << 7) - 1;
    private const int ExplorerShellLaunchMaxInputChars = 32_767;
    private const string ExplorerShellLaunchExport = "deskbox_explorer_shell_launch_v1";

    internal ExplorerShellLaunchNativeCallResult OpenThroughExplorerShell(
        string path,
        string workingDirectory,
        string verb)
    {
        if ((Capabilities & ExplorerShellLaunchCapability) == 0)
        {
            return ExplorerShellLaunchCallFailure(
                ExplorerShellLaunchNativeCallFailure.CapabilityUnavailable,
                $"Native Explorer-shell capability 0x{ExplorerShellLaunchCapability:X} is unavailable; module mask is 0x{Capabilities:X}.");
        }

        string normalizedWorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? string.Empty
            : workingDirectory;
        string normalizedVerb = string.IsNullOrWhiteSpace(verb)
            ? string.Empty
            : verb;
        if (!IsValidExplorerShellInput(path, allowEmpty: false) ||
            !IsValidExplorerShellInput(normalizedWorkingDirectory, allowEmpty: true) ||
            !IsValidExplorerShellInput(normalizedVerb, allowEmpty: true))
        {
            return ExplorerShellLaunchCallFailure(
                ExplorerShellLaunchNativeCallFailure.InvalidInput,
                "Explorer-shell launch input is empty where required, too long, or contains an embedded NUL.");
        }

        if (!NativeLibrary.TryGetExport(_module, ExplorerShellLaunchExport, out nint exportAddress))
        {
            return ExplorerShellLaunchCallFailure(
                ExplorerShellLaunchNativeCallFailure.MissingExport,
                $"The DeskBox native export '{ExplorerShellLaunchExport}' is missing.");
        }

        var launchExport =
            (delegate* unmanaged[Cdecl]<NativeExplorerShellLaunchRequest*, NativeExplorerShellLaunchResult*, uint>)
            (void*)exportAddress;
        fixed (char* pathPointer = path)
        fixed (char* workingDirectoryPointer = normalizedWorkingDirectory)
        fixed (char* verbPointer = normalizedVerb)
        {
            var request = new NativeExplorerShellLaunchRequest
            {
                StructSize = (uint)sizeof(NativeExplorerShellLaunchRequest),
                StructVersion = ExplorerShellLaunchStructVersion,
                Path = new NativeUtf16String(pathPointer, path.Length),
                WorkingDirectory = new NativeUtf16String(
                    workingDirectoryPointer,
                    normalizedWorkingDirectory.Length),
                Verb = new NativeUtf16String(verbPointer, normalizedVerb.Length)
            };
            var result = new NativeExplorerShellLaunchResult
            {
                StructSize = (uint)sizeof(NativeExplorerShellLaunchResult),
                StructVersion = ExplorerShellLaunchStructVersion
            };

            uint returnedStatus = launchExport(&request, &result);
            if (result.StructSize != (uint)sizeof(NativeExplorerShellLaunchResult) ||
                result.StructVersion != ExplorerShellLaunchStructVersion)
            {
                return FromExplorerShellLaunchResult(
                    ExplorerShellLaunchNativeCallFailure.InvalidNativeResult,
                    $"Native Explorer-shell result envelope mismatch: size={result.StructSize}, version={result.StructVersion}.",
                    result);
            }

            if (returnedStatus != result.Status ||
                (result.AttemptedPhases & ~ExplorerShellLaunchAttemptedPhasesMask) != 0 ||
                result.OperationSucceeded > 1 ||
                result.Reserved0 != 0 ||
                result.Reserved1 != 0 ||
                result.Reserved2 != 0 ||
                result.Reserved3 != 0 ||
                result.Reserved4 != 0 ||
                (result.Status == StatusOk) != (result.OperationSucceeded == 1))
            {
                return FromExplorerShellLaunchResult(
                    ExplorerShellLaunchNativeCallFailure.InvalidNativeResult,
                    $"Native Explorer-shell result is inconsistent: return={returnedStatus}, result={result.Status}.",
                    result);
            }

            if (result.Status != StatusOk)
            {
                return FromExplorerShellLaunchResult(
                    ExplorerShellLaunchNativeCallFailure.NativeFailure,
                    BuildExplorerShellFailureDetail(result),
                    result);
            }

            return FromExplorerShellLaunchResult(
                ExplorerShellLaunchNativeCallFailure.None,
                string.Empty,
                result);
        }
    }

    private static bool IsValidExplorerShellInput(string? value, bool allowEmpty)
    {
        return value is not null &&
               (allowEmpty || value.Length > 0) &&
               value.Length <= ExplorerShellLaunchMaxInputChars &&
               !value.Contains('\0');
    }

    private static string BuildExplorerShellFailureDetail(NativeExplorerShellLaunchResult result)
    {
        // Per-phase HRESULTs pinpoint where the Explorer delegation died: the
        // aggregate OperationHResult is often 0 while the failing phase carries
        // the real code (feedback #9: phases=0xF, aggregate 0x00000000, but the
        // desktop phase was the one that could not find the shell window).
        return $"Native Explorer-shell launch failed: status={result.Status}, " +
               $"HRESULT=0x{result.OperationHResult:X8}, phases=0x{result.AttemptedPhases:X}, " +
               $"phaseHr com=0x{result.ComHResult:X8} create=0x{result.CreateHResult:X8} " +
               $"windows=0x{result.WindowsHResult:X8} desktop=0x{result.DesktopHResult:X8} " +
               $"document=0x{result.DocumentHResult:X8} application=0x{result.ApplicationHResult:X8} " +
               $"execute=0x{result.ExecuteHResult:X8}.";
    }

    private static ExplorerShellLaunchNativeCallResult ExplorerShellLaunchCallFailure(
        ExplorerShellLaunchNativeCallFailure failure,
        string detail)
    {
        return new ExplorerShellLaunchNativeCallResult(
            failure,
            detail,
            0,
            HResultNotAttempted,
            0,
            HResultNotAttempted,
            HResultNotAttempted,
            HResultNotAttempted,
            HResultNotAttempted,
            HResultNotAttempted,
            HResultNotAttempted,
            HResultNotAttempted);
    }

    private static ExplorerShellLaunchNativeCallResult FromExplorerShellLaunchResult(
        ExplorerShellLaunchNativeCallFailure failure,
        string detail,
        NativeExplorerShellLaunchResult result)
    {
        return new ExplorerShellLaunchNativeCallResult(
            failure,
            detail,
            result.Status,
            result.OperationHResult,
            result.AttemptedPhases,
            result.ComHResult,
            result.CreateHResult,
            result.WindowsHResult,
            result.DesktopHResult,
            result.DocumentHResult,
            result.ApplicationHResult,
            result.ExecuteHResult);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeExplorerShellLaunchRequest
    {
        internal uint StructSize;
        internal uint StructVersion;
        internal uint Flags;
        internal uint Reserved0;
        internal NativeUtf16String Path;
        internal NativeUtf16String WorkingDirectory;
        internal NativeUtf16String Verb;
        internal ulong Reserved1;
        internal ulong Reserved2;
        internal ulong Reserved3;
        internal ulong Reserved4;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeExplorerShellLaunchResult
    {
        internal uint StructSize;
        internal uint StructVersion;
        internal uint Status;
        internal int OperationHResult;
        internal uint AttemptedPhases;
        internal int ComHResult;
        internal int CreateHResult;
        internal int WindowsHResult;
        internal int DesktopHResult;
        internal int DocumentHResult;
        internal int ApplicationHResult;
        internal int ExecuteHResult;
        internal uint OperationSucceeded;
        internal uint Reserved0;
        internal ulong Reserved1;
        internal ulong Reserved2;
        internal ulong Reserved3;
        internal ulong Reserved4;
    }
}

internal static class ExplorerShellLaunchNativeBackend
{
    internal static ExplorerShellLaunchNativeCallResult TryOpen(
        string path,
        string workingDirectory,
        string verb)
    {
        ShortcutNativeLoadResult load = ShortcutNativeModule.Default;
        if (!load.Success)
        {
            return new ExplorerShellLaunchNativeCallResult(
                ExplorerShellLaunchNativeCallFailure.ModuleUnavailable,
                $"{load.Failure}: {load.Detail}",
                0,
                ShortcutNativeModule.HResultNotAttempted,
                0,
                ShortcutNativeModule.HResultNotAttempted,
                ShortcutNativeModule.HResultNotAttempted,
                ShortcutNativeModule.HResultNotAttempted,
                ShortcutNativeModule.HResultNotAttempted,
                ShortcutNativeModule.HResultNotAttempted,
                ShortcutNativeModule.HResultNotAttempted,
                ShortcutNativeModule.HResultNotAttempted);
        }

        return load.Module!.OpenThroughExplorerShell(path, workingDirectory, verb);
    }
}
