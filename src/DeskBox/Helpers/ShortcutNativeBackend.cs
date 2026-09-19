using System.ComponentModel;
using DeskBox.Platform;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace DeskBox.Helpers;

internal enum ShortcutBackendMode
{
    CSharp,
    Rust
}

internal static class ShortcutBackendPolicy
{
    internal const string EnvironmentVariable = "DESKBOX_SHORTCUT_BACKEND";

#if DESKBOX_NATIVE_AOT
    internal static ShortcutBackendMode Current { get; } = ShortcutBackendMode.Rust;
#else
    internal static ShortcutBackendMode Current { get; } = Resolve(
        Environment.GetEnvironmentVariable(EnvironmentVariable),
        RuntimeFeature.IsDynamicCodeSupported);

    internal static ShortcutBackendMode Resolve(string? configuredValue, bool isDynamicCodeSupported)
    {
        if (!isDynamicCodeSupported)
        {
            // Native AOT must never fall back to the legacy ComImport backend.
            return ShortcutBackendMode.Rust;
        }

        return string.Equals(configuredValue?.Trim(), "rust", StringComparison.OrdinalIgnoreCase)
            ? ShortcutBackendMode.Rust
            : ShortcutBackendMode.CSharp;
    }
#endif
}

internal enum ShortcutNativeLoadFailure
{
    None,
    UnsupportedArchitecture,
    MissingModule,
    LoadFailed,
    MissingExport,
    AbiMismatch
}

internal sealed record ShortcutNativeLoadResult(
    ShortcutNativeModule? Module,
    ShortcutNativeLoadFailure Failure,
    string Detail)
{
    internal bool Success => Module is not null && Failure == ShortcutNativeLoadFailure.None;
}

internal sealed record ShortcutNativeDiagnosticState(
    string SelectedBackend,
    string ModuleName,
    bool ModuleExists,
    string? ModuleArchitecture,
    string? ModuleSha256,
    bool LoadAttempted,
    string LoadState,
    uint? AbiVersion,
    ulong? Capabilities);

internal enum ShortcutNativeCallFailure
{
    None,
    ModuleUnavailable,
    CapabilityUnavailable,
    NativeFailure,
    InvalidNativeResult
}

internal sealed record ShortcutNativeCallResult(
    ShortcutInfo? Metadata,
    ShortcutNativeCallFailure Failure,
    string Detail,
    uint Status,
    int OperationHResult,
    int ComHResult,
    int CreateHResult,
    int LoadHResult,
    int ResolveHResult,
    int TargetHResult,
    int DescriptionHResult,
    int ArgumentsHResult,
    int WorkingDirectoryHResult,
    int IconHResult,
    uint AttemptedPhases,
    uint AttemptedFields,
    uint SucceededFields,
    uint PresentFields,
    uint CallerBufferTooSmallFields,
    uint SourceTruncatedFields)
{
    internal bool Success => Metadata is not null && Failure == ShortcutNativeCallFailure.None;
}

internal sealed record ShortcutNativeWriteCallResult(
    ShortcutNativeCallFailure Failure,
    string Detail,
    uint Status,
    int OperationHResult,
    int ComHResult,
    int CreateHResult,
    int SaveHResult,
    int TargetHResult,
    int DescriptionHResult,
    int ArgumentsHResult,
    int WorkingDirectoryHResult,
    int IconHResult,
    uint AttemptedPhases,
    uint AttemptedFields,
    uint SucceededFields)
{
    internal bool Success => Failure == ShortcutNativeCallFailure.None;
}

internal sealed record ShortcutNativeUiResolveCallResult(
    ShortcutNativeCallFailure Failure,
    string Detail,
    uint Status,
    int OperationHResult,
    int ComHResult,
    int CreateHResult,
    int LoadHResult,
    int ResolveHResult,
    uint AttemptedPhases,
    uint ResolveFlags)
{
    internal bool Success => Failure == ShortcutNativeCallFailure.None;
}

/// <summary>
/// Loads and invokes the versioned DeskBox native module ABI. The default module
/// path is fixed to the application directory; arbitrary search-path resolution
/// is intentionally not supported.
/// </summary>
internal sealed unsafe partial class ShortcutNativeModule
{
    internal const string DllName = "deskbox_native.dll";
    internal const uint AbiVersion = 2;
    internal const ulong StoredRawCapability = 1UL << 0;
    internal const ulong EffectiveDiagnosticCapability = 1UL << 1;
    internal const ulong ResolveNoUiCapability = 1UL << 2;
    internal const ulong WriteCapability = 1UL << 3;
    internal const ulong ResolveWithUiCapability = 1UL << 4;
    internal const uint WriteShellNamespaceTargetFlag = 1U << 0;

    internal const uint StatusOk = 0;
    internal const uint ModeStoredRaw = 1;
    internal const uint ModeEffectiveDiagnostic = 2;
    internal const int HResultNotAttempted = unchecked((int)0x8000000A);

    private const uint StructVersion = 2;
    private const int StoredFieldCapacity = 260;
    private const int DiagnosticArgumentCapacity = 512;
    private const int MaxInputPathChars = 32_767;
    private const int MaxInputValueChars = 32_767;
    private const uint LoadLibrarySearchDllLoadDir = 0x00000100;
    private const uint LoadLibrarySearchSystem32 = 0x00000800;

    private const string AbiVersionExport = "deskbox_native_abi_version";
    private const string CapabilitiesExport = "deskbox_native_capabilities";
    private const string ReadExport = "deskbox_shortcut_read_v2";
    private const string ResolveExport = "deskbox_shortcut_resolve_no_ui_v2";
    private const string WriteExport = "deskbox_shortcut_write_v2";
    private const string ResolveWithUiExport = "deskbox_shortcut_resolve_with_ui_v2";

    private static readonly Lazy<ShortcutNativeLoadResult> s_default = new(
        () => Load(Path.Combine(AppContext.BaseDirectory, DllName)),
        LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly nint _module;
    private readonly delegate* unmanaged[Cdecl]<uint> _abiVersion;
    private readonly delegate* unmanaged[Cdecl]<ulong> _capabilities;
    private readonly delegate* unmanaged[Cdecl]<NativeReadRequest*, NativeReadResult*, uint> _read;
    private readonly delegate* unmanaged[Cdecl]<NativeResolveRequest*, NativeReadResult*, uint> _resolve;
    private readonly delegate* unmanaged[Cdecl]<NativeWriteRequest*, NativeWriteResult*, uint> _write;
    private readonly delegate* unmanaged[Cdecl]<NativeUiResolveRequest*, NativeUiResolveResult*, uint> _resolveWithUi;

    private ShortcutNativeModule(
        string modulePath,
        nint module,
        nint abiVersion,
        nint capabilities,
        nint read,
        nint resolve,
        nint write,
        nint resolveWithUi,
        ulong capabilityMask)
    {
        ModulePath = modulePath;
        _module = module;
        _abiVersion = (delegate* unmanaged[Cdecl]<uint>)(void*)abiVersion;
        _capabilities = (delegate* unmanaged[Cdecl]<ulong>)(void*)capabilities;
        _read = (delegate* unmanaged[Cdecl]<NativeReadRequest*, NativeReadResult*, uint>)(void*)read;
        _resolve = (delegate* unmanaged[Cdecl]<NativeResolveRequest*, NativeReadResult*, uint>)(void*)resolve;
        _write = (delegate* unmanaged[Cdecl]<NativeWriteRequest*, NativeWriteResult*, uint>)(void*)write;
        _resolveWithUi = (delegate* unmanaged[Cdecl]<NativeUiResolveRequest*, NativeUiResolveResult*, uint>)(void*)resolveWithUi;
        Capabilities = capabilityMask;
    }

    internal string ModulePath { get; }

    internal nint ModuleHandle => _module;

    internal ulong Capabilities { get; }

    internal static ShortcutNativeLoadResult Default => s_default.Value;

    internal static bool IsDefaultLoadCreated => s_default.IsValueCreated;

    internal static bool TryGetCachedDefault(out ShortcutNativeLoadResult? result)
    {
        if (!s_default.IsValueCreated)
        {
            result = null;
            return false;
        }

        result = s_default.Value;
        return true;
    }

    internal static ShortcutNativeLoadResult Load(string modulePath)
    {
        if (RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
        {
            return Failure(
                ShortcutNativeLoadFailure.UnsupportedArchitecture,
                $"DeskBox native module ABI supports x64 and ARM64; process architecture is {RuntimeInformation.ProcessArchitecture}.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(modulePath);
        }
        catch (Exception ex)
        {
            return Failure(ShortcutNativeLoadFailure.MissingModule, ex.Message);
        }

        if (!File.Exists(fullPath))
        {
            return Failure(
                ShortcutNativeLoadFailure.MissingModule,
                $"Native shortcut module was not found at '{fullPath}'.");
        }

        nint module = Kernel32NativeMethods.LoadLibraryEx(
            fullPath,
            0,
            LoadLibrarySearchDllLoadDir | LoadLibrarySearchSystem32);
        if (module == 0)
        {
            int error = Marshal.GetLastPInvokeError();
            return Failure(
                ShortcutNativeLoadFailure.LoadFailed,
                new Win32Exception(error).Message + $" (0x{error:X8})");
        }

        bool moduleOwnershipTransferred = false;
        try
        {
            if (!TryGetRequiredExport(module, AbiVersionExport, out nint abiVersion, out string detail) ||
                !TryGetRequiredExport(module, CapabilitiesExport, out nint capabilities, out detail) ||
                !TryGetRequiredExport(module, ReadExport, out nint read, out detail) ||
                !TryGetRequiredExport(module, ResolveExport, out nint resolve, out detail) ||
                !TryGetRequiredExport(module, WriteExport, out nint write, out detail) ||
                !TryGetRequiredExport(module, ResolveWithUiExport, out nint resolveWithUi, out detail))
            {
                return Failure(ShortcutNativeLoadFailure.MissingExport, detail);
            }

            var abiProbe = (delegate* unmanaged[Cdecl]<uint>)(void*)abiVersion;
            uint actualAbi = abiProbe();
            if (actualAbi != AbiVersion)
            {
                return Failure(
                    ShortcutNativeLoadFailure.AbiMismatch,
                    $"DeskBox native module ABI mismatch: expected {AbiVersion}, found {actualAbi}.");
            }

            var capabilitiesProbe = (delegate* unmanaged[Cdecl]<ulong>)(void*)capabilities;
            ulong capabilityMask = capabilitiesProbe();
            var nativeModule = new ShortcutNativeModule(
                fullPath,
                module,
                abiVersion,
                capabilities,
                read,
                resolve,
                write,
                resolveWithUi,
                capabilityMask);
            moduleOwnershipTransferred = true;
            return new ShortcutNativeLoadResult(
                nativeModule,
                ShortcutNativeLoadFailure.None,
                string.Empty);
        }
        catch (Exception ex)
        {
            return Failure(ShortcutNativeLoadFailure.LoadFailed, ex.Message);
        }
        finally
        {
            if (!moduleOwnershipTransferred)
            {
                NativeLibrary.Free(module);
            }
        }
    }

    internal ShortcutNativeCallResult ReadStoredRaw(string shortcutPath)
    {
        return Invoke(shortcutPath, ModeStoredRaw, timeoutMs: null);
    }

    internal ShortcutNativeCallResult ReadEffectiveDiagnostic(string shortcutPath)
    {
        return Invoke(shortcutPath, ModeEffectiveDiagnostic, timeoutMs: null);
    }

    internal ShortcutNativeCallResult ResolveNoUi(string shortcutPath, ushort timeoutMs = 0)
    {
        return Invoke(shortcutPath, ModeStoredRaw, timeoutMs);
    }

    internal ShortcutNativeUiResolveCallResult ResolveWithUi(
        string shortcutPath,
        nint ownerHwnd)
    {
        if ((Capabilities & ResolveWithUiCapability) == 0)
        {
            return UiResolveCallFailure(
                ShortcutNativeCallFailure.CapabilityUnavailable,
                $"Native shortcut capability 0x{ResolveWithUiCapability:X} is unavailable; module mask is 0x{Capabilities:X}.");
        }

        if (!IsValidInputValue(shortcutPath, allowEmpty: false))
        {
            return UiResolveCallFailure(
                ShortcutNativeCallFailure.NativeFailure,
                "Shortcut path is empty, too long, or contains an embedded NUL.");
        }

        fixed (char* shortcutPathPointer = shortcutPath)
        {
            var request = new NativeUiResolveRequest
            {
                StructSize = (uint)sizeof(NativeUiResolveRequest),
                StructVersion = StructVersion,
                ShortcutPath = new NativeUtf16String(shortcutPathPointer, shortcutPath.Length),
                OwnerHwnd = (ulong)(nuint)ownerHwnd
            };
            var result = new NativeUiResolveResult
            {
                StructSize = (uint)sizeof(NativeUiResolveResult),
                StructVersion = StructVersion
            };

            uint returnedStatus = _resolveWithUi(&request, &result);
            if (result.StructSize != (uint)sizeof(NativeUiResolveResult) ||
                result.StructVersion != StructVersion)
            {
                return FromUiResolveResult(
                    ShortcutNativeCallFailure.InvalidNativeResult,
                    $"Native UI resolve result envelope mismatch: size={result.StructSize}, version={result.StructVersion}.",
                    result);
            }

            if (returnedStatus != result.Status)
            {
                return FromUiResolveResult(
                    ShortcutNativeCallFailure.InvalidNativeResult,
                    $"Native UI resolve status mismatch: return={returnedStatus}, result={result.Status}.",
                    result);
            }

            if (result.Status != StatusOk)
            {
                return FromUiResolveResult(
                    ShortcutNativeCallFailure.NativeFailure,
                    $"Native shortcut UI resolve failed: status={result.Status}, HRESULT=0x{result.OperationHResult:X8}.",
                    result);
            }

            return FromUiResolveResult(ShortcutNativeCallFailure.None, string.Empty, result);
        }
    }

    internal ShortcutNativeWriteCallResult WriteShortcut(
        string shortcutPath,
        ShortcutInfo metadata)
    {
        return WriteShortcutCore(shortcutPath, metadata, flags: 0);
    }

    internal ShortcutNativeWriteCallResult WriteShellNamespaceShortcut(
        string shortcutPath,
        string parsingName,
        string description)
    {
        return WriteShortcutCore(
            shortcutPath,
            new ShortcutInfo(
                parsingName,
                description,
                string.Empty,
                string.Empty,
                string.Empty,
                0),
            WriteShellNamespaceTargetFlag);
    }

    private ShortcutNativeWriteCallResult WriteShortcutCore(
        string shortcutPath,
        ShortcutInfo metadata,
        uint flags)
    {
        if ((Capabilities & WriteCapability) == 0)
        {
            return WriteCallFailure(
                ShortcutNativeCallFailure.CapabilityUnavailable,
                $"Native shortcut capability 0x{WriteCapability:X} is unavailable; module mask is 0x{Capabilities:X}.");
        }

        if (!IsValidInputValue(shortcutPath, allowEmpty: false) ||
            !IsValidInputValue(metadata.TargetPath, allowEmpty: false) ||
            !IsValidInputValue(metadata.Description, allowEmpty: true) ||
            !IsValidInputValue(metadata.Arguments, allowEmpty: true) ||
            !IsValidInputValue(metadata.WorkingDirectory, allowEmpty: true) ||
            !IsValidInputValue(metadata.IconLocation, allowEmpty: true))
        {
            return WriteCallFailure(
                ShortcutNativeCallFailure.NativeFailure,
                "Shortcut write values are empty where required, too long, or contain an embedded NUL.");
        }

        fixed (char* shortcutPathPointer = shortcutPath)
        fixed (char* targetPointer = metadata.TargetPath)
        fixed (char* descriptionPointer = metadata.Description)
        fixed (char* argumentsPointer = metadata.Arguments)
        fixed (char* workingDirectoryPointer = metadata.WorkingDirectory)
        fixed (char* iconPointer = metadata.IconLocation)
        {
            var request = new NativeWriteRequest
            {
                StructSize = (uint)sizeof(NativeWriteRequest),
                StructVersion = StructVersion,
                Flags = flags,
                IconIndex = metadata.IconIndex,
                ShortcutPath = new NativeUtf16String(shortcutPathPointer, shortcutPath.Length),
                TargetPath = new NativeUtf16String(targetPointer, metadata.TargetPath.Length),
                Description = new NativeUtf16String(descriptionPointer, metadata.Description.Length),
                Arguments = new NativeUtf16String(argumentsPointer, metadata.Arguments.Length),
                WorkingDirectory = new NativeUtf16String(
                    workingDirectoryPointer,
                    metadata.WorkingDirectory.Length),
                IconPath = new NativeUtf16String(iconPointer, metadata.IconLocation.Length)
            };
            var result = new NativeWriteResult
            {
                StructSize = (uint)sizeof(NativeWriteResult),
                StructVersion = StructVersion
            };

            uint returnedStatus = _write(&request, &result);
            if (result.StructSize != (uint)sizeof(NativeWriteResult) ||
                result.StructVersion != StructVersion)
            {
                return FromWriteResult(
                    ShortcutNativeCallFailure.InvalidNativeResult,
                    $"Native write result envelope mismatch: size={result.StructSize}, version={result.StructVersion}.",
                    result);
            }

            if (returnedStatus != result.Status)
            {
                return FromWriteResult(
                    ShortcutNativeCallFailure.InvalidNativeResult,
                    $"Native write status mismatch: return={returnedStatus}, result={result.Status}.",
                    result);
            }

            if (result.Status != StatusOk)
            {
                return FromWriteResult(
                    ShortcutNativeCallFailure.NativeFailure,
                    $"Native shortcut write failed: status={result.Status}, HRESULT=0x{result.OperationHResult:X8}.",
                    result);
            }

            return FromWriteResult(ShortcutNativeCallFailure.None, string.Empty, result);
        }
    }

    internal uint ProbeAbiVersion() => _abiVersion();

    internal ulong ProbeCapabilities() => _capabilities();

    private static bool IsValidInputValue(string? value, bool allowEmpty)
    {
        return value is not null &&
               value.Length <= MaxInputValueChars &&
               (allowEmpty || value.Length > 0) &&
               !value.Contains('\0');
    }

    private ShortcutNativeCallResult Invoke(string shortcutPath, uint mode, ushort? timeoutMs)
    {
        ulong requiredCapability = timeoutMs.HasValue
            ? ResolveNoUiCapability
            : mode == ModeStoredRaw
                ? StoredRawCapability
                : EffectiveDiagnosticCapability;
        if ((Capabilities & requiredCapability) == 0)
        {
            return CallFailure(
                ShortcutNativeCallFailure.CapabilityUnavailable,
                $"Native shortcut capability 0x{requiredCapability:X} is unavailable; module mask is 0x{Capabilities:X}.");
        }

        if (string.IsNullOrEmpty(shortcutPath) ||
            shortcutPath.Length > MaxInputPathChars ||
            shortcutPath.Contains('\0'))
        {
            return CallFailure(
                ShortcutNativeCallFailure.NativeFailure,
                "Shortcut path is empty, too long, or contains an embedded NUL.");
        }

        int argumentCapacity = mode == ModeEffectiveDiagnostic
            ? DiagnosticArgumentCapacity
            : StoredFieldCapacity;
        char[] target = new char[StoredFieldCapacity];
        char[] description = new char[StoredFieldCapacity];
        char[] arguments = new char[argumentCapacity];
        char[] workingDirectory = new char[StoredFieldCapacity];
        char[] icon = new char[StoredFieldCapacity];

        fixed (char* pathPointer = shortcutPath)
        fixed (char* targetPointer = target)
        fixed (char* descriptionPointer = description)
        fixed (char* argumentsPointer = arguments)
        fixed (char* workingDirectoryPointer = workingDirectory)
        fixed (char* iconPointer = icon)
        {
            var readRequest = new NativeReadRequest
            {
                StructSize = (uint)sizeof(NativeReadRequest),
                StructVersion = StructVersion,
                Mode = mode,
                ShortcutPath = pathPointer,
                ShortcutPathLengthChars = (uint)shortcutPath.Length,
                TargetPath = new NativeUtf16Buffer(targetPointer, target.Length),
                Description = new NativeUtf16Buffer(descriptionPointer, description.Length),
                Arguments = new NativeUtf16Buffer(argumentsPointer, arguments.Length),
                WorkingDirectory = new NativeUtf16Buffer(workingDirectoryPointer, workingDirectory.Length),
                IconPath = new NativeUtf16Buffer(iconPointer, icon.Length)
            };
            var result = new NativeReadResult
            {
                StructSize = (uint)sizeof(NativeReadResult),
                StructVersion = StructVersion
            };

            uint returnedStatus;
            if (timeoutMs.HasValue)
            {
                var resolveRequest = new NativeResolveRequest
                {
                    StructSize = (uint)sizeof(NativeResolveRequest),
                    StructVersion = StructVersion,
                    TimeoutMs = timeoutMs.Value,
                    ReadRequest = readRequest
                };
                returnedStatus = _resolve(&resolveRequest, &result);
            }
            else
            {
                returnedStatus = _read(&readRequest, &result);
            }

            if (result.StructSize != (uint)sizeof(NativeReadResult) ||
                result.StructVersion != StructVersion)
            {
                return FromResult(
                    null,
                    ShortcutNativeCallFailure.InvalidNativeResult,
                    $"Native result envelope mismatch: size={result.StructSize}, version={result.StructVersion}.",
                    result);
            }

            if (returnedStatus != result.Status)
            {
                return FromResult(
                    null,
                    ShortcutNativeCallFailure.InvalidNativeResult,
                    $"Native status mismatch: return={returnedStatus}, result={result.Status}.",
                    result);
            }

            if (result.Status != StatusOk)
            {
                return FromResult(
                    null,
                    ShortcutNativeCallFailure.NativeFailure,
                    $"Native shortcut operation failed: status={result.Status}, HRESULT=0x{result.OperationHResult:X8}.",
                    result);
            }

            if (!TryDecode(target, result.TargetRequiredChars, out string targetValue) ||
                !TryDecode(arguments, result.ArgumentsRequiredChars, out string argumentValue))
            {
                return FromResult(
                    null,
                    ShortcutNativeCallFailure.InvalidNativeResult,
                    "Native shortcut result contains invalid target or argument lengths.",
                    result);
            }

            string descriptionValue = string.Empty;
            string workingDirectoryValue = string.Empty;
            string iconValue = string.Empty;
            if (mode == ModeStoredRaw &&
                (!TryDecode(description, result.DescriptionRequiredChars, out descriptionValue) ||
                 !TryDecode(workingDirectory, result.WorkingDirectoryRequiredChars, out workingDirectoryValue) ||
                 !TryDecode(icon, result.IconRequiredChars, out iconValue)))
            {
                return FromResult(
                    null,
                    ShortcutNativeCallFailure.InvalidNativeResult,
                    "Native shortcut result contains invalid optional-field lengths.",
                    result);
            }

            var metadata = new ShortcutInfo(
                targetValue,
                descriptionValue,
                argumentValue,
                workingDirectoryValue,
                iconValue,
                result.IconIndex);
            return FromResult(
                metadata,
                ShortcutNativeCallFailure.None,
                string.Empty,
                result);
        }
    }

    private static bool TryDecode(char[] buffer, uint requiredChars, out string value)
    {
        value = string.Empty;
        if (requiredChars == 0 || requiredChars > (uint)buffer.Length)
        {
            return false;
        }

        int requiredLength = checked((int)requiredChars);
        if (buffer[requiredLength - 1] != '\0')
        {
            return false;
        }

        value = new string(buffer, 0, requiredLength - 1);
        return true;
    }

    private static bool TryGetRequiredExport(
        nint module,
        string name,
        out nint address,
        out string detail)
    {
        if (NativeLibrary.TryGetExport(module, name, out address))
        {
            detail = string.Empty;
            return true;
        }

        detail = $"The DeskBox native export '{name}' is missing.";
        return false;
    }

    private static ShortcutNativeLoadResult Failure(
        ShortcutNativeLoadFailure failure,
        string detail)
    {
        return new ShortcutNativeLoadResult(null, failure, detail);
    }

    private static ShortcutNativeCallResult CallFailure(
        ShortcutNativeCallFailure failure,
        string detail)
    {
        return new ShortcutNativeCallResult(
            null,
            failure,
            detail,
            0,
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
            0,
            0,
            0,
            0,
            0,
            0);
    }

    private static ShortcutNativeCallResult FromResult(
        ShortcutInfo? metadata,
        ShortcutNativeCallFailure failure,
        string detail,
        NativeReadResult result)
    {
        return new ShortcutNativeCallResult(
            metadata,
            failure,
            detail,
            result.Status,
            result.OperationHResult,
            result.ComHResult,
            result.CreateHResult,
            result.LoadHResult,
            result.ResolveHResult,
            result.TargetHResult,
            result.DescriptionHResult,
            result.ArgumentsHResult,
            result.WorkingDirectoryHResult,
            result.IconHResult,
            result.AttemptedPhases,
            result.AttemptedFields,
            result.SucceededFields,
            result.PresentFields,
            result.CallerBufferTooSmallFields,
            result.SourceTruncatedFields);
    }

    private static ShortcutNativeWriteCallResult WriteCallFailure(
        ShortcutNativeCallFailure failure,
        string detail)
    {
        return new ShortcutNativeWriteCallResult(
            failure,
            detail,
            0,
            0,
            HResultNotAttempted,
            HResultNotAttempted,
            HResultNotAttempted,
            HResultNotAttempted,
            HResultNotAttempted,
            HResultNotAttempted,
            HResultNotAttempted,
            HResultNotAttempted,
            0,
            0,
            0);
    }

    private static ShortcutNativeWriteCallResult FromWriteResult(
        ShortcutNativeCallFailure failure,
        string detail,
        NativeWriteResult result)
    {
        return new ShortcutNativeWriteCallResult(
            failure,
            detail,
            result.Status,
            result.OperationHResult,
            result.ComHResult,
            result.CreateHResult,
            result.SaveHResult,
            result.TargetHResult,
            result.DescriptionHResult,
            result.ArgumentsHResult,
            result.WorkingDirectoryHResult,
            result.IconHResult,
            result.AttemptedPhases,
            result.AttemptedFields,
            result.SucceededFields);
    }

    private static ShortcutNativeUiResolveCallResult UiResolveCallFailure(
        ShortcutNativeCallFailure failure,
        string detail)
    {
        return new ShortcutNativeUiResolveCallResult(
            failure,
            detail,
            0,
            0,
            HResultNotAttempted,
            HResultNotAttempted,
            HResultNotAttempted,
            HResultNotAttempted,
            0,
            0);
    }

    private static ShortcutNativeUiResolveCallResult FromUiResolveResult(
        ShortcutNativeCallFailure failure,
        string detail,
        NativeUiResolveResult result)
    {
        return new ShortcutNativeUiResolveCallResult(
            failure,
            detail,
            result.Status,
            result.OperationHResult,
            result.ComHResult,
            result.CreateHResult,
            result.LoadHResult,
            result.ResolveHResult,
            result.AttemptedPhases,
            result.ResolveFlags);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct NativeUtf16Buffer
    {
        internal NativeUtf16Buffer(char* data, int capacityChars)
        {
            Data = data;
            CapacityChars = (uint)capacityChars;
            Reserved0 = 0;
        }

        internal readonly char* Data;
        internal readonly uint CapacityChars;
        internal readonly uint Reserved0;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct NativeUtf16String
    {
        internal NativeUtf16String(char* data, int lengthChars)
        {
            Data = lengthChars == 0 ? null : data;
            LengthChars = (uint)lengthChars;
            Reserved0 = 0;
        }

        internal readonly char* Data;
        internal readonly uint LengthChars;
        internal readonly uint Reserved0;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeReadRequest
    {
        internal uint StructSize;
        internal uint StructVersion;
        internal uint Mode;
        internal uint Flags;
        internal char* ShortcutPath;
        internal uint ShortcutPathLengthChars;
        internal uint Reserved0;
        internal NativeUtf16Buffer TargetPath;
        internal NativeUtf16Buffer Description;
        internal NativeUtf16Buffer Arguments;
        internal NativeUtf16Buffer WorkingDirectory;
        internal NativeUtf16Buffer IconPath;
        internal ulong Reserved1;
        internal ulong Reserved2;
        internal ulong Reserved3;
        internal ulong Reserved4;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeReadResult
    {
        internal uint StructSize;
        internal uint StructVersion;
        internal uint Status;
        internal int OperationHResult;
        internal uint AttemptedPhases;
        internal int ComHResult;
        internal int CreateHResult;
        internal int LoadHResult;
        internal int ResolveHResult;
        internal uint AttemptedFields;
        internal uint SucceededFields;
        internal uint PresentFields;
        internal uint CallerBufferTooSmallFields;
        internal uint SourceTruncatedFields;
        internal int TargetHResult;
        internal int DescriptionHResult;
        internal int ArgumentsHResult;
        internal int WorkingDirectoryHResult;
        internal int IconHResult;
        internal int IconIndex;
        internal uint TargetRequiredChars;
        internal uint DescriptionRequiredChars;
        internal uint ArgumentsRequiredChars;
        internal uint WorkingDirectoryRequiredChars;
        internal uint IconRequiredChars;
        internal uint Reserved0;
        internal ulong Reserved1;
        internal ulong Reserved2;
        internal ulong Reserved3;
        internal ulong Reserved4;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeResolveRequest
    {
        internal uint StructSize;
        internal uint StructVersion;
        internal uint TimeoutMs;
        internal uint Flags;
        internal NativeReadRequest ReadRequest;
        internal ulong Reserved1;
        internal ulong Reserved2;
        internal ulong Reserved3;
        internal ulong Reserved4;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeWriteRequest
    {
        internal uint StructSize;
        internal uint StructVersion;
        internal uint Flags;
        internal int IconIndex;
        internal NativeUtf16String ShortcutPath;
        internal NativeUtf16String TargetPath;
        internal NativeUtf16String Description;
        internal NativeUtf16String Arguments;
        internal NativeUtf16String WorkingDirectory;
        internal NativeUtf16String IconPath;
        internal ulong Reserved1;
        internal ulong Reserved2;
        internal ulong Reserved3;
        internal ulong Reserved4;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeWriteResult
    {
        internal uint StructSize;
        internal uint StructVersion;
        internal uint Status;
        internal int OperationHResult;
        internal uint AttemptedPhases;
        internal int ComHResult;
        internal int CreateHResult;
        internal int SaveHResult;
        internal uint AttemptedFields;
        internal uint SucceededFields;
        internal int TargetHResult;
        internal int DescriptionHResult;
        internal int ArgumentsHResult;
        internal int WorkingDirectoryHResult;
        internal int IconHResult;
        internal uint Reserved0;
        internal ulong Reserved1;
        internal ulong Reserved2;
        internal ulong Reserved3;
        internal ulong Reserved4;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeUiResolveRequest
    {
        internal uint StructSize;
        internal uint StructVersion;
        internal uint Flags;
        internal uint Reserved0;
        internal NativeUtf16String ShortcutPath;
        internal ulong OwnerHwnd;
        internal ulong Reserved1;
        internal ulong Reserved2;
        internal ulong Reserved3;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeUiResolveResult
    {
        internal uint StructSize;
        internal uint StructVersion;
        internal uint Status;
        internal int OperationHResult;
        internal uint AttemptedPhases;
        internal int ComHResult;
        internal int CreateHResult;
        internal int LoadHResult;
        internal int ResolveHResult;
        internal uint ResolveFlags;
        internal ulong Reserved1;
        internal ulong Reserved2;
        internal ulong Reserved3;
    }
}

internal static class ShortcutNativeBackend
{
    internal static ShortcutNativeDiagnosticState CaptureDiagnosticState()
    {
        string modulePath = Path.Combine(AppContext.BaseDirectory, ShortcutNativeModule.DllName);
        bool moduleExists = File.Exists(modulePath);
        bool loadAttempted = ShortcutNativeModule.TryGetCachedDefault(
            out ShortcutNativeLoadResult? cachedLoad);
        bool loaded = cachedLoad?.Success == true;

        return new ShortcutNativeDiagnosticState(
            ShortcutBackendPolicy.Current.ToString(),
            ShortcutNativeModule.DllName,
            moduleExists,
            moduleExists ? ReadPeArchitecture(modulePath) : null,
            moduleExists ? ComputeSha256(modulePath) : null,
            loadAttempted,
            loadAttempted
                ? loaded
                    ? "Loaded"
                    : cachedLoad!.Failure.ToString()
                : "NotProbed",
            loaded ? ShortcutNativeModule.AbiVersion : null,
            loaded ? cachedLoad!.Module!.Capabilities : null);
    }

    internal static ShortcutNativeCallResult ReadStoredRaw(string shortcutPath)
    {
        return Invoke(module => module.ReadStoredRaw(shortcutPath));
    }

    internal static ShortcutNativeCallResult ReadEffectiveDiagnostic(string shortcutPath)
    {
        return Invoke(module => module.ReadEffectiveDiagnostic(shortcutPath));
    }

    internal static ShortcutNativeCallResult ResolveNoUi(string shortcutPath, ushort timeoutMs = 0)
    {
        return Invoke(module => module.ResolveNoUi(shortcutPath, timeoutMs));
    }

    internal static ShortcutNativeUiResolveCallResult ResolveWithUi(
        string shortcutPath,
        nint ownerHwnd)
    {
        ShortcutNativeLoadResult load = ShortcutNativeModule.Default;
        if (!load.Success)
        {
            return new ShortcutNativeUiResolveCallResult(
                ShortcutNativeCallFailure.ModuleUnavailable,
                $"{load.Failure}: {load.Detail}",
                0,
                0,
                ShortcutNativeModule.HResultNotAttempted,
                ShortcutNativeModule.HResultNotAttempted,
                ShortcutNativeModule.HResultNotAttempted,
                ShortcutNativeModule.HResultNotAttempted,
                0,
                0);
        }

        return load.Module!.ResolveWithUi(shortcutPath, ownerHwnd);
    }

    internal static ShortcutNativeWriteCallResult WriteShortcut(
        string shortcutPath,
        ShortcutInfo metadata)
    {
        ShortcutNativeLoadResult load = ShortcutNativeModule.Default;
        if (!load.Success)
        {
            return new ShortcutNativeWriteCallResult(
                ShortcutNativeCallFailure.ModuleUnavailable,
                $"{load.Failure}: {load.Detail}",
                0,
                0,
                ShortcutNativeModule.HResultNotAttempted,
                ShortcutNativeModule.HResultNotAttempted,
                ShortcutNativeModule.HResultNotAttempted,
                ShortcutNativeModule.HResultNotAttempted,
                ShortcutNativeModule.HResultNotAttempted,
                ShortcutNativeModule.HResultNotAttempted,
                ShortcutNativeModule.HResultNotAttempted,
                ShortcutNativeModule.HResultNotAttempted,
                0,
                0,
                0);
        }

        return load.Module!.WriteShortcut(shortcutPath, metadata);
    }

    internal static ShortcutNativeWriteCallResult WriteShellNamespaceShortcut(
        string shortcutPath,
        string parsingName,
        string description)
    {
        ShortcutNativeLoadResult load = ShortcutNativeModule.Default;
        if (!load.Success)
        {
            return new ShortcutNativeWriteCallResult(
                ShortcutNativeCallFailure.ModuleUnavailable,
                $"{load.Failure}: {load.Detail}",
                0,
                0,
                ShortcutNativeModule.HResultNotAttempted,
                ShortcutNativeModule.HResultNotAttempted,
                ShortcutNativeModule.HResultNotAttempted,
                ShortcutNativeModule.HResultNotAttempted,
                ShortcutNativeModule.HResultNotAttempted,
                ShortcutNativeModule.HResultNotAttempted,
                ShortcutNativeModule.HResultNotAttempted,
                ShortcutNativeModule.HResultNotAttempted,
                0,
                0,
                0);
        }

        return load.Module!.WriteShellNamespaceShortcut(
            shortcutPath,
            parsingName,
            description);
    }

    private static ShortcutNativeCallResult Invoke(
        Func<ShortcutNativeModule, ShortcutNativeCallResult> operation)
    {
        ShortcutNativeLoadResult load = ShortcutNativeModule.Default;
        if (!load.Success)
        {
            return new ShortcutNativeCallResult(
                null,
                ShortcutNativeCallFailure.ModuleUnavailable,
                $"{load.Failure}: {load.Detail}",
                0,
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
                0,
                0,
                0,
                0,
                0,
                0);
        }

        return operation(load.Module!);
    }

    private static string ReadPeArchitecture(string modulePath)
    {
        try
        {
            using FileStream stream = File.OpenRead(modulePath);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 0x40 || reader.ReadUInt16() != 0x5A4D)
            {
                return "Unknown";
            }

            stream.Position = 0x3C;
            int peOffset = reader.ReadInt32();
            if (peOffset < 0 || peOffset > stream.Length - 6)
            {
                return "Unknown";
            }

            stream.Position = peOffset;
            if (reader.ReadUInt32() != 0x00004550)
            {
                return "Unknown";
            }

            return reader.ReadUInt16() switch
            {
                0x8664 => "X64",
                0xAA64 => "Arm64",
                _ => "Unknown"
            };
        }
        catch
        {
            return "Unknown";
        }
    }

    private static string? ComputeSha256(string modulePath)
    {
        try
        {
            using FileStream stream = File.OpenRead(modulePath);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch
        {
            return null;
        }
    }
}
