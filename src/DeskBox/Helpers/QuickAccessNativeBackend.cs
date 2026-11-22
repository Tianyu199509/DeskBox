using System.Runtime.InteropServices;

namespace DeskBox.Helpers;

internal enum QuickAccessBackendMode
{
    Rust
}

internal static class QuickAccessBackendPolicy
{
    internal static QuickAccessBackendMode Current => QuickAccessBackendMode.Rust;
}

internal enum QuickAccessNativeOperation : uint
{
    QueryPinState = 1,
    Pin = 2,
    Unpin = 3
}

internal enum QuickAccessNativeCallFailure
{
    None,
    ModuleUnavailable,
    CapabilityUnavailable,
    MissingExport,
    InvalidInput,
    NativeFailure,
    InvalidNativeResult
}

internal sealed record QuickAccessNativeCallResult(
    QuickAccessNativeCallFailure Failure,
    string Detail,
    uint Status,
    int OperationHResult,
    uint AttemptedPhases,
    int ComHResult,
    int CreateHResult,
    int QuickNamespaceHResult,
    int ItemsHResult,
    int EnumerateHResult,
    int ItemPathHResult,
    int PropertyHResult,
    int ParentNamespaceHResult,
    int ParseNameHResult,
    int InvokeHResult,
    QuickAccessPinState PinState,
    bool MatchedItem,
    bool FallbackUsed)
{
    internal bool Success => Failure == QuickAccessNativeCallFailure.None;
}

internal sealed unsafe partial class ShortcutNativeModule
{
    internal const ulong QuickAccessCapability = 1UL << 7;

    private const uint QuickAccessStructVersion = 1;
    private const uint QuickAccessAttemptedPhasesMask = (1U << 10) - 1;
    private const int QuickAccessMaxInputChars = 32_767;
    private const string QuickAccessExport = "deskbox_quick_access_v1";

    internal QuickAccessNativeCallResult InvokeQuickAccess(
        QuickAccessNativeOperation operation,
        string folderPath,
        string parentPath,
        string folderName)
    {
        if ((Capabilities & QuickAccessCapability) == 0)
        {
            return QuickAccessCallFailure(
                QuickAccessNativeCallFailure.CapabilityUnavailable,
                $"Native Quick Access capability 0x{QuickAccessCapability:X} is unavailable; module mask is 0x{Capabilities:X}.");
        }

        bool isQuery = operation == QuickAccessNativeOperation.QueryPinState;
        if (!Enum.IsDefined(operation) ||
            !IsValidQuickAccessInput(folderPath, allowEmpty: false) ||
            !IsValidQuickAccessInput(parentPath, allowEmpty: isQuery) ||
            !IsValidQuickAccessInput(folderName, allowEmpty: isQuery) ||
            (isQuery && (parentPath.Length != 0 || folderName.Length != 0)))
        {
            return QuickAccessCallFailure(
                QuickAccessNativeCallFailure.InvalidInput,
                "Quick Access input is empty where required, too long, or contains an embedded NUL.");
        }

        if (!NativeLibrary.TryGetExport(_module, QuickAccessExport, out nint exportAddress))
        {
            return QuickAccessCallFailure(
                QuickAccessNativeCallFailure.MissingExport,
                $"The DeskBox native export '{QuickAccessExport}' is missing.");
        }

        var quickAccessExport =
            (delegate* unmanaged[Cdecl]<NativeQuickAccessRequest*, NativeQuickAccessResult*, uint>)
            (void*)exportAddress;
        fixed (char* folderPathPointer = folderPath)
        fixed (char* parentPathPointer = parentPath)
        fixed (char* folderNamePointer = folderName)
        {
            var request = new NativeQuickAccessRequest
            {
                StructSize = (uint)sizeof(NativeQuickAccessRequest),
                StructVersion = QuickAccessStructVersion,
                Operation = (uint)operation,
                FolderPath = new NativeUtf16String(folderPathPointer, folderPath.Length),
                ParentPath = new NativeUtf16String(parentPathPointer, parentPath.Length),
                FolderName = new NativeUtf16String(folderNamePointer, folderName.Length)
            };
            var result = new NativeQuickAccessResult
            {
                StructSize = (uint)sizeof(NativeQuickAccessResult),
                StructVersion = QuickAccessStructVersion
            };

            uint returnedStatus = quickAccessExport(&request, &result);
            if (result.StructSize != (uint)sizeof(NativeQuickAccessResult) ||
                result.StructVersion != QuickAccessStructVersion)
            {
                return FromQuickAccessResult(
                    QuickAccessNativeCallFailure.InvalidNativeResult,
                    $"Native Quick Access result envelope mismatch: size={result.StructSize}, version={result.StructVersion}.",
                    result);
            }

            if (returnedStatus != result.Status ||
                (result.AttemptedPhases & ~QuickAccessAttemptedPhasesMask) != 0 ||
                result.PinState > (uint)QuickAccessPinState.Pinned ||
                result.OperationSucceeded > 1 ||
                result.MatchedItem > 1 ||
                result.FallbackUsed > 1 ||
                result.Reserved0 != 0 ||
                result.Reserved1 != 0 ||
                result.Reserved2 != 0 ||
                result.Reserved3 != 0 ||
                result.Reserved4 != 0 ||
                (result.Status == StatusOk) != (result.OperationSucceeded == 1))
            {
                return FromQuickAccessResult(
                    QuickAccessNativeCallFailure.InvalidNativeResult,
                    $"Native Quick Access result is inconsistent: return={returnedStatus}, result={result.Status}.",
                    result);
            }

            if (result.Status != StatusOk)
            {
                return FromQuickAccessResult(
                    QuickAccessNativeCallFailure.NativeFailure,
                    BuildQuickAccessFailureDetail(result),
                    result);
            }

            return FromQuickAccessResult(
                QuickAccessNativeCallFailure.None,
                string.Empty,
                result);
        }
    }

    private static bool IsValidQuickAccessInput(string? value, bool allowEmpty)
    {
        return value is not null &&
               (allowEmpty || value.Length > 0) &&
               value.Length <= QuickAccessMaxInputChars &&
               !value.Contains('\0');
    }

    private static string BuildQuickAccessFailureDetail(NativeQuickAccessResult result)
    {
        return $"Native Quick Access operation failed: status={result.Status}, " +
               $"HRESULT=0x{result.OperationHResult:X8}, phases=0x{result.AttemptedPhases:X}.";
    }

    private static QuickAccessNativeCallResult QuickAccessCallFailure(
        QuickAccessNativeCallFailure failure,
        string detail)
    {
        return new QuickAccessNativeCallResult(
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
            HResultNotAttempted,
            HResultNotAttempted,
            HResultNotAttempted,
            HResultNotAttempted,
            QuickAccessPinState.Unknown,
            false,
            false);
    }

    private static QuickAccessNativeCallResult FromQuickAccessResult(
        QuickAccessNativeCallFailure failure,
        string detail,
        NativeQuickAccessResult result)
    {
        return new QuickAccessNativeCallResult(
            failure,
            detail,
            result.Status,
            result.OperationHResult,
            result.AttemptedPhases,
            result.ComHResult,
            result.CreateHResult,
            result.QuickNamespaceHResult,
            result.ItemsHResult,
            result.EnumerateHResult,
            result.ItemPathHResult,
            result.PropertyHResult,
            result.ParentNamespaceHResult,
            result.ParseNameHResult,
            result.InvokeHResult,
            (QuickAccessPinState)result.PinState,
            result.MatchedItem != 0,
            result.FallbackUsed != 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeQuickAccessRequest
    {
        internal uint StructSize;
        internal uint StructVersion;
        internal uint Operation;
        internal uint Flags;
        internal NativeUtf16String FolderPath;
        internal NativeUtf16String ParentPath;
        internal NativeUtf16String FolderName;
        internal ulong Reserved1;
        internal ulong Reserved2;
        internal ulong Reserved3;
        internal ulong Reserved4;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeQuickAccessResult
    {
        internal uint StructSize;
        internal uint StructVersion;
        internal uint Status;
        internal int OperationHResult;
        internal uint AttemptedPhases;
        internal int ComHResult;
        internal int CreateHResult;
        internal int QuickNamespaceHResult;
        internal int ItemsHResult;
        internal int EnumerateHResult;
        internal int ItemPathHResult;
        internal int PropertyHResult;
        internal int ParentNamespaceHResult;
        internal int ParseNameHResult;
        internal int InvokeHResult;
        internal uint PinState;
        internal uint OperationSucceeded;
        internal uint MatchedItem;
        internal uint FallbackUsed;
        internal uint Reserved0;
        internal ulong Reserved1;
        internal ulong Reserved2;
        internal ulong Reserved3;
        internal ulong Reserved4;
    }
}

internal static class QuickAccessNativeBackend
{
    internal static QuickAccessNativeCallResult Invoke(
        QuickAccessNativeOperation operation,
        string folderPath,
        string parentPath,
        string folderName)
    {
        ShortcutNativeLoadResult load = ShortcutNativeModule.Default;
        if (!load.Success)
        {
            return new QuickAccessNativeCallResult(
                QuickAccessNativeCallFailure.ModuleUnavailable,
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
                ShortcutNativeModule.HResultNotAttempted,
                ShortcutNativeModule.HResultNotAttempted,
                ShortcutNativeModule.HResultNotAttempted,
                ShortcutNativeModule.HResultNotAttempted,
                QuickAccessPinState.Unknown,
                false,
                false);
        }

        return load.Module!.InvokeQuickAccess(operation, folderPath, parentPath, folderName);
    }
}
