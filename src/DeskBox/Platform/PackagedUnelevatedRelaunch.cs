using System.Runtime.InteropServices;

namespace DeskBox.Platform;

/// <summary>
/// Relaunches a packaged (MSIX / Microsoft Store) application at Explorer's
/// Medium integrity by delegating <c>shell:AppsFolder\&lt;AUMID&gt;</c>
/// activation (with command-line arguments) through the desktop Explorer's
/// hosted <c>IShellDispatch2::ShellExecute</c> automation object.
/// </summary>
internal static unsafe partial class PackagedUnelevatedRelaunch
{
    private const uint CoInitApartmentThreaded = 0x2;
    private const uint ClsctxInprocServer = 0x1;
    private const uint ClsctxLocalServer = 0x4;
    private const int SwcDesktop = 8;
    private const int SwfoNeedDispatch = 1;
    private const int SwShowNormal = 1;
    private const ushort VtEmpty = 0;
    private const ushort VtI4 = 3;
    private const ushort VtBstr = 8;
    private const ushort VtDispatch = 9;
    private const ushort DispatchMethod = 0x1;
    private const ushort DispatchPropertyGet = 0x2;
    private const uint LcidNeutral = 0x0400;

    private static readonly Guid ClsidShellWindows =
        new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");

    private static readonly Guid IidIShellWindows =
        new("85CB6900-4D95-11CF-960C-0080C7F4EE85");

    private static readonly Guid IidNull = Guid.Empty;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeVariant
    {
        public ushort Vt;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public nint Data1;
        public nint Data2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispParams
    {
        public NativeVariant* Rgvarg;
        public int* RgdispidNamedArgs;
        public uint CArgs;
        public uint CNamedArgs;
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint pvReserved, uint dwCoInit);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(
        in Guid rclsid,
        nint pUnkOuter,
        uint dwClsContext,
        in Guid riid,
        out nint ppv);

    [LibraryImport("ole32.dll")]
    private static partial int CoAllowSetForegroundWindow(
        nint pUnk,
        nint lpvReserved);

    [LibraryImport("oleaut32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint SysAllocString(string psz);

    [LibraryImport("oleaut32.dll")]
    private static partial void SysFreeString(nint bstrString);

    [LibraryImport("oleaut32.dll")]
    private static partial int VariantClear(ref NativeVariant pvarg);

    public static bool TryRelaunchPackagedAppViaExplorer(
        string appUserModelId,
        string? arguments,
        Action<string>? log = null)
    {
        if (!TryFormatAppsFolderTarget(appUserModelId, out string appsFolderTarget))
        {
            log?.Invoke("[PackagedUnelevatedRelaunch] Invalid AUMID for AppsFolder relaunch");
            return false;
        }

        return RunShellExecuteOnStaWorker(
            appsFolderTarget,
            arguments ?? string.Empty,
            string.Empty,
            log);
    }

    /// <summary>
    /// Launches an executable or shell target at Explorer's Medium integrity via
    /// the desktop shell's <c>IShellDispatch2::ShellExecute</c> automation
    /// object. Used as the secondary unelevated relaunch channel when parent-
    /// process handle parenting is blocked on Direct installs.
    /// </summary>
    public static bool TryShellExecuteViaExplorer(
        string filePath,
        string? arguments,
        string? workingDirectory = null,
        Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        return RunShellExecuteOnStaWorker(
            filePath.Trim(),
            arguments ?? string.Empty,
            workingDirectory ?? string.Empty,
            log);
    }

    private static bool RunShellExecuteOnStaWorker(
        string target,
        string arguments,
        string workingDirectory,
        Action<string>? log)
    {
        bool succeeded = false;
        var worker = new Thread(() =>
        {
            succeeded = ExecuteOnStaThread(target, arguments, workingDirectory, log);
        })
        {
            IsBackground = true,
            Name = "DeskBox-PackagedUnelevatedRelaunch"
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        if (!worker.Join(TimeSpan.FromSeconds(5)))
        {
            log?.Invoke("[PackagedUnelevatedRelaunch] Timed out waiting for Explorer COM activation");
            return false;
        }

        return succeeded;
    }

    internal static bool TryFormatAppsFolderTarget(
        string? appUserModelId,
        out string appsFolderTarget)
    {
        appsFolderTarget = string.Empty;
        if (string.IsNullOrWhiteSpace(appUserModelId))
        {
            return false;
        }

        string trimmed = appUserModelId.Trim();
        if (!trimmed.Contains('!', StringComparison.Ordinal) ||
            trimmed.IndexOfAny(['\\', '/', '\0', '"']) >= 0)
        {
            return false;
        }

        appsFolderTarget = $"shell:AppsFolder\\{trimmed}";
        return true;
    }

    private static bool ExecuteOnStaThread(
        string appsFolderTarget,
        string arguments,
        string workingDirectory,
        Action<string>? log)
    {
        int coInitHr = CoInitializeEx(0, CoInitApartmentThreaded);
        bool shouldUninit = coInitHr is 0 or 1;
        if (coInitHr < 0)
        {
            log?.Invoke($"[PackagedUnelevatedRelaunch] CoInitializeEx failed hr=0x{coInitHr:X8}");
            return false;
        }

        nint shellWindows = 0;
        nint desktopBrowser = 0;
        nint documentDispatch = 0;
        nint shellDispatch = 0;
        try
        {
            int hr = CoCreateInstance(
                in ClsidShellWindows,
                0,
                ClsctxLocalServer | ClsctxInprocServer,
                in IidIShellWindows,
                out shellWindows);
            if (hr < 0 || shellWindows == 0)
            {
                log?.Invoke($"[PackagedUnelevatedRelaunch] CoCreateInstance(ShellWindows) failed hr=0x{hr:X8}");
                return false;
            }

            // IShellWindows::FindWindowSW sits at vtable slot 15 (7 IDispatch + 8 IShellWindows methods).
            NativeVariant varLoc = default;
            NativeVariant varLocRoot = default;
            int desktopHwnd = 0;
            nint* vtable = *(nint**)shellWindows;
            var findWindowSw = (delegate* unmanaged[Stdcall]<
                nint,
                NativeVariant*,
                NativeVariant*,
                int,
                int*,
                int,
                nint*,
                int>)vtable[15];
            nint foundDispatch = 0;
            hr = findWindowSw(
                shellWindows,
                &varLoc,
                &varLocRoot,
                SwcDesktop,
                &desktopHwnd,
                SwfoNeedDispatch,
                &foundDispatch);
            desktopBrowser = foundDispatch;
            if (hr < 0 || desktopBrowser == 0)
            {
                log?.Invoke($"[PackagedUnelevatedRelaunch] FindWindowSW(SWC_DESKTOP) failed hr=0x{hr:X8}");
                return false;
            }

            if (!TryGetDispatchProperty(desktopBrowser, "Document", out documentDispatch, log) ||
                !TryGetDispatchProperty(documentDispatch, "Application", out shellDispatch, log))
            {
                return false;
            }

            _ = CoAllowSetForegroundWindow(shellDispatch, 0);
            return InvokeShellExecute(shellDispatch, appsFolderTarget, arguments, workingDirectory, log);
        }
        catch (Exception ex)
        {
            log?.Invoke($"[PackagedUnelevatedRelaunch] Unexpected failure: {ex.Message}");
            return false;
        }
        finally
        {
            if (shellDispatch != 0) Marshal.Release(shellDispatch);
            if (documentDispatch != 0) Marshal.Release(documentDispatch);
            if (desktopBrowser != 0) Marshal.Release(desktopBrowser);
            if (shellWindows != 0) Marshal.Release(shellWindows);
            if (shouldUninit) CoUninitialize();
        }
    }

    private static bool TryGetDispatchProperty(
        nint dispatch,
        string memberName,
        out nint resultDispatch,
        Action<string>? log)
    {
        resultDispatch = 0;
        if (!TryGetDispId(dispatch, memberName, out int dispId, log))
        {
            return false;
        }

        DispParams emptyParams = default;
        NativeVariant result = default;
        try
        {
            nint* vtable = *(nint**)dispatch;
            var invoke = (delegate* unmanaged[Stdcall]<
                nint,
                int,
                Guid*,
                uint,
                ushort,
                DispParams*,
                NativeVariant*,
                nint,
                nint,
                int>)vtable[6];
            Guid iidNull = IidNull;
            int hr = invoke(
                dispatch,
                dispId,
                &iidNull,
                LcidNeutral,
                DispatchPropertyGet,
                &emptyParams,
                &result,
                0,
                0);
            if (hr < 0 || result.Vt != VtDispatch || result.Data1 == 0)
            {
                log?.Invoke($"[PackagedUnelevatedRelaunch] get_{memberName} failed hr=0x{hr:X8} vt={result.Vt}");
                return false;
            }

            resultDispatch = result.Data1;
            result.Vt = VtEmpty;
            result.Data1 = 0;
            return true;
        }
        finally
        {
            _ = VariantClear(ref result);
        }
    }

    private static bool InvokeShellExecute(
        nint shellDispatch,
        string file,
        string arguments,
        string workingDirectory,
        Action<string>? log)
    {
        if (!TryGetDispId(shellDispatch, "ShellExecute", out int dispId, log))
        {
            return false;
        }

        nint bstrFile = SysAllocString(file);
        nint bstrArgs = SysAllocString(arguments);
        nint bstrDir = SysAllocString(workingDirectory);
        nint bstrVerb = SysAllocString("open");
        try
        {
            if (bstrFile == 0 || bstrArgs == 0 || bstrDir == 0 || bstrVerb == 0)
            {
                return false;
            }

            // IDispatch::Invoke expects positional arguments in reverse order.
            NativeVariant* args = stackalloc NativeVariant[5];
            args[0] = new NativeVariant { Vt = VtI4, Data1 = SwShowNormal };
            args[1] = new NativeVariant { Vt = VtBstr, Data1 = bstrVerb };
            args[2] = new NativeVariant { Vt = VtBstr, Data1 = bstrDir };
            args[3] = new NativeVariant { Vt = VtBstr, Data1 = bstrArgs };
            args[4] = new NativeVariant { Vt = VtBstr, Data1 = bstrFile };

            var dispParams = new DispParams
            {
                Rgvarg = args,
                RgdispidNamedArgs = null,
                CArgs = 5,
                CNamedArgs = 0
            };
            NativeVariant invokeResult = default;
            try
            {
                nint* vtable = *(nint**)shellDispatch;
                var invoke = (delegate* unmanaged[Stdcall]<
                    nint,
                    int,
                    Guid*,
                    uint,
                    ushort,
                    DispParams*,
                    NativeVariant*,
                    nint,
                    nint,
                    int>)vtable[6];
                Guid iidNull = IidNull;
                int hr = invoke(
                    shellDispatch,
                    dispId,
                    &iidNull,
                    LcidNeutral,
                    DispatchMethod,
                    &dispParams,
                    &invokeResult,
                    0,
                    0);
                if (hr < 0)
                {
                    log?.Invoke($"[PackagedUnelevatedRelaunch] ShellExecute('{file}') failed hr=0x{hr:X8}");
                    return false;
                }

                return true;
            }
            finally
            {
                _ = VariantClear(ref invokeResult);
            }
        }
        finally
        {
            if (bstrVerb != 0) SysFreeString(bstrVerb);
            if (bstrDir != 0) SysFreeString(bstrDir);
            if (bstrArgs != 0) SysFreeString(bstrArgs);
            if (bstrFile != 0) SysFreeString(bstrFile);
        }
    }

    private static bool TryGetDispId(
        nint dispatch,
        string memberName,
        out int dispId,
        Action<string>? log)
    {
        dispId = -1;
        fixed (char* pName = memberName)
        {
            char* namePtr = pName;
            int resolved = -1;
            nint* vtable = *(nint**)dispatch;
            var getIdsOfNames = (delegate* unmanaged[Stdcall]<
                nint,
                Guid*,
                char**,
                uint,
                uint,
                int*,
                int>)vtable[5];
            Guid iidNull = IidNull;
            int hr = getIdsOfNames(
                dispatch,
                &iidNull,
                &namePtr,
                1,
                LcidNeutral,
                &resolved);
            if (hr < 0)
            {
                log?.Invoke($"[PackagedUnelevatedRelaunch] GetIDsOfNames('{memberName}') failed hr=0x{hr:X8}");
                return false;
            }

            dispId = resolved;
            return true;
        }
    }
}
