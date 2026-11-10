using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace DeskBox.Platform;

internal enum DeskBoxQjsNativeStatus : int
{
    Ok = 0,
    Error = 1,
    Timeout = 2,
    OutOfMemory = 3,
    InvalidArgument = 4,
    ExportNotFound = 5,
    EngineUnavailable = 6
}

internal readonly record struct DeskBoxQjsNativeCompileOutcome(
    DeskBoxQjsNativeStatus Status,
    byte[] Bytecode,
    string ExportsJson,
    string ErrorMessage);

internal readonly record struct DeskBoxQjsNativeInvokeOutcome(
    DeskBoxQjsNativeStatus Status,
    string ResultJson,
    string ErrorMessage);

/// <summary>
/// AOT-safe native interop boundary for <c>deskbox_qjs.dll</c> (QuickJS-NG v0.18.0 sandbox).
/// </summary>
internal static partial class CustomWidgetQuickJsInterop
{
    internal const string DllName = "deskbox_qjs";
    internal const uint ExpectedAbiVersion = 1;

    private static int _availabilityState; // 0 = unknown, 1 = available, -1 = unavailable
    private static string? _unavailableReason;

    [LibraryImport(DllName, EntryPoint = "deskbox_qjs_abi_version")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [DefaultDllImportSearchPaths(DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.System32)]
    private static partial uint NativeAbiVersion();

    [LibraryImport(DllName, EntryPoint = "deskbox_qjs_compile_module")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [DefaultDllImportSearchPaths(DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.System32)]
    private static unsafe partial int NativeCompileModule(
        byte* sourceUtf8,
        nuint sourceLen,
        byte* moduleNameUtf8,
        uint timeoutMs,
        nuint memoryLimitBytes,
        out nint outBytecode,
        out nuint outBytecodeLen,
        out nint outExportsJsonUtf8,
        out nuint outExportsJsonLen,
        out nint outErrorUtf8,
        out nuint outErrorLen);

    [LibraryImport(DllName, EntryPoint = "deskbox_qjs_invoke_bytecode")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [DefaultDllImportSearchPaths(DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.System32)]
    private static unsafe partial int NativeInvokeBytecode(
        byte* bytecode,
        nuint bytecodeLen,
        byte* fnNameUtf8,
        int requireExport,
        byte* payloadJsonUtf8,
        nuint payloadJsonLen,
        uint timeoutMs,
        nuint memoryLimitBytes,
        out nint outResultJsonUtf8,
        out nuint outResultJsonLen,
        out nint outErrorUtf8,
        out nuint outErrorLen);

    [LibraryImport(DllName, EntryPoint = "deskbox_qjs_free_buffer")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [DefaultDllImportSearchPaths(DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.System32)]
    private static partial void NativeFreeBuffer(nint buffer);

    internal static bool IsAvailable(out string? reason)
    {
        int state = Volatile.Read(ref _availabilityState);
        if (state == 1)
        {
            reason = null;
            return true;
        }
        if (state == -1)
        {
            reason = _unavailableReason;
            return false;
        }

        try
        {
            uint abi = NativeAbiVersion();
            if (abi != ExpectedAbiVersion)
            {
                _unavailableReason = $"deskbox_qjs ABI mismatch (expected {ExpectedAbiVersion}, got {abi}).";
                Volatile.Write(ref _availabilityState, -1);
                reason = _unavailableReason;
                return false;
            }

            Volatile.Write(ref _availabilityState, 1);
            reason = null;
            return true;
        }
        catch (Exception ex)
        {
            _unavailableReason = $"deskbox_qjs unavailable: {ex.Message}";
            Volatile.Write(ref _availabilityState, -1);
            reason = _unavailableReason;
            return false;
        }
    }

    internal static unsafe DeskBoxQjsNativeCompileOutcome CompileModule(
        ReadOnlySpan<byte> sourceUtf8,
        string moduleName,
        uint timeoutMs,
        nuint memoryLimitBytes)
    {
        if (!IsAvailable(out string? unavailableReason))
        {
            return new DeskBoxQjsNativeCompileOutcome(
                DeskBoxQjsNativeStatus.EngineUnavailable,
                [],
                string.Empty,
                unavailableReason ?? "deskbox_qjs is unavailable.");
        }

        byte[] nameBytes = BuildNullTerminatedUtf8(string.IsNullOrWhiteSpace(moduleName) ? "logic.js" : moduleName);
        nint outBytecode = 0;
        nuint outBytecodeLen = 0;
        nint outExports = 0;
        nuint outExportsLen = 0;
        nint outError = 0;
        nuint outErrorLen = 0;

        try
        {
            int rawStatus;
            fixed (byte* srcPtr = sourceUtf8)
            fixed (byte* namePtr = nameBytes)
            {
                byte dummy = 0;
                byte* effectiveSrcPtr = sourceUtf8.Length > 0 ? srcPtr : &dummy;
                rawStatus = NativeCompileModule(
                    effectiveSrcPtr,
                    (nuint)sourceUtf8.Length,
                    namePtr,
                    timeoutMs,
                    memoryLimitBytes,
                    out outBytecode,
                    out outBytecodeLen,
                    out outExports,
                    out outExportsLen,
                    out outError,
                    out outErrorLen);
            }

            byte[] bytecode = CopyBuffer(outBytecode, outBytecodeLen);
            string exportsJson = DecodeUtf8Buffer(outExports, outExportsLen);
            string errorMsg = DecodeUtf8Buffer(outError, outErrorLen);
            return new DeskBoxQjsNativeCompileOutcome(
                MapStatus(rawStatus),
                bytecode,
                exportsJson,
                errorMsg);
        }
        finally
        {
            if (outBytecode != 0) NativeFreeBuffer(outBytecode);
            if (outExports != 0) NativeFreeBuffer(outExports);
            if (outError != 0) NativeFreeBuffer(outError);
        }
    }

    internal static unsafe DeskBoxQjsNativeInvokeOutcome InvokeBytecode(
        ReadOnlySpan<byte> bytecode,
        string fnName,
        bool requireExport,
        ReadOnlySpan<byte> payloadJsonUtf8,
        uint timeoutMs,
        nuint memoryLimitBytes)
    {
        if (!IsAvailable(out string? unavailableReason))
        {
            return new DeskBoxQjsNativeInvokeOutcome(
                DeskBoxQjsNativeStatus.EngineUnavailable,
                string.Empty,
                unavailableReason ?? "deskbox_qjs is unavailable.");
        }

        byte[] fnBytes = BuildNullTerminatedUtf8(fnName);
        nint outResult = 0;
        nuint outResultLen = 0;
        nint outError = 0;
        nuint outErrorLen = 0;

        try
        {
            int rawStatus;
            fixed (byte* bcPtr = bytecode)
            fixed (byte* fnPtr = fnBytes)
            fixed (byte* payloadPtr = payloadJsonUtf8)
            {
                byte dummyBc = 0;
                byte dummyPayload = 0;
                rawStatus = NativeInvokeBytecode(
                    bytecode.Length > 0 ? bcPtr : &dummyBc,
                    (nuint)bytecode.Length,
                    fnPtr,
                    requireExport ? 1 : 0,
                    payloadJsonUtf8.Length > 0 ? payloadPtr : &dummyPayload,
                    (nuint)payloadJsonUtf8.Length,
                    timeoutMs,
                    memoryLimitBytes,
                    out outResult,
                    out outResultLen,
                    out outError,
                    out outErrorLen);
            }

            string resultJson = DecodeUtf8Buffer(outResult, outResultLen);
            string errorMsg = DecodeUtf8Buffer(outError, outErrorLen);
            return new DeskBoxQjsNativeInvokeOutcome(
                MapStatus(rawStatus),
                resultJson,
                errorMsg);
        }
        finally
        {
            if (outResult != 0) NativeFreeBuffer(outResult);
            if (outError != 0) NativeFreeBuffer(outError);
        }
    }

    private static DeskBoxQjsNativeStatus MapStatus(int status) => status switch
    {
        0 => DeskBoxQjsNativeStatus.Ok,
        1 => DeskBoxQjsNativeStatus.Error,
        2 => DeskBoxQjsNativeStatus.Timeout,
        3 => DeskBoxQjsNativeStatus.OutOfMemory,
        4 => DeskBoxQjsNativeStatus.InvalidArgument,
        5 => DeskBoxQjsNativeStatus.ExportNotFound,
        _ => DeskBoxQjsNativeStatus.Error
    };

    private static byte[] BuildNullTerminatedUtf8(string value)
    {
        int byteCount = Encoding.UTF8.GetByteCount(value);
        byte[] buffer = new byte[byteCount + 1];
        Encoding.UTF8.GetBytes(value, 0, value.Length, buffer, 0);
        buffer[byteCount] = 0;
        return buffer;
    }

    private static unsafe byte[] CopyBuffer(nint ptr, nuint len)
    {
        if (ptr == 0 || len == 0)
        {
            return [];
        }

        int length = checked((int)len);
        byte[] copy = new byte[length];
        new ReadOnlySpan<byte>((void*)ptr, length).CopyTo(copy);
        return copy;
    }

    private static unsafe string DecodeUtf8Buffer(nint ptr, nuint len)
    {
        if (ptr == 0 || len == 0)
        {
            return string.Empty;
        }

        int length = checked((int)len);
        return Encoding.UTF8.GetString((byte*)ptr, length);
    }
}
