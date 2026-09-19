using DeskBox.Platform;
using System.Runtime.InteropServices;

namespace DeskBox.Services;

/// <summary>
/// Reads Task Scheduler's UTF-16 BSTR directly. Redirected schtasks XML uses a
/// legacy code page that can differ from DeskBox's UTF-8 process code page.
/// Raw COM keeps this path available in both JIT and Native AOT builds.
/// </summary>
internal static unsafe partial class DirectStartupTaskXmlReader
{
    // Slots include IUnknown and IDispatch, as declared in taskschd.h.
    private const int ReleaseSlot = 2;
    private const int GetFolderSlot = 7;
    private const int ConnectSlot = 10;
    private const int GetTaskSlot = 13;
    private const int GetXmlSlot = 20;
    private const int RpcChangedMode = unchecked((int)0x80010106);
    private static readonly Guid TaskSchedulerClassId =
        new("0F87369F-A4E5-4CFC-BD3E-73E6154572DD");
    private static readonly Guid TaskServiceInterfaceId =
        new("2FABA4C7-4DA9-4013-9697-20CC3FD40F85");

    internal static string Read(string taskName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskName);
        int initialized = Ole32NativeMethods.CoInitializeEx(0, 0); // COINIT_MULTITHREADED
        if (initialized < 0 && initialized != RpcChangedMode)
        {
            Marshal.ThrowExceptionForHR(initialized);
        }

        nint service = 0;
        nint folder = 0;
        nint task = 0;
        nint rootName = 0;
        nint name = 0;
        nint xml = 0;
        try
        {
            Marshal.ThrowExceptionForHR(Ole32NativeMethods.CoCreateInstance(
                in TaskSchedulerClassId, 0, 1, in TaskServiceInterfaceId, out service));

            // VT_EMPTY connects to the local scheduler using the current token.
            var connect = (delegate* unmanaged[Stdcall]<
                nint, EmptyVariant, EmptyVariant, EmptyVariant, EmptyVariant, int>)
                GetSlot(service, ConnectSlot);
            Marshal.ThrowExceptionForHR(connect(service, default, default, default, default));

            rootName = Marshal.StringToBSTR("\\");
            var getFolder = (delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)
                GetSlot(service, GetFolderSlot);
            Marshal.ThrowExceptionForHR(getFolder(service, rootName, &folder));

            name = Marshal.StringToBSTR(taskName);
            var getTask = (delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)
                GetSlot(folder, GetTaskSlot);
            Marshal.ThrowExceptionForHR(getTask(folder, name, &task));

            var getXml = (delegate* unmanaged[Stdcall]<nint, nint*, int>)
                GetSlot(task, GetXmlSlot);
            Marshal.ThrowExceptionForHR(getXml(task, &xml));
            return xml == 0 ? string.Empty : Marshal.PtrToStringBSTR(xml);
        }
        finally
        {
            Marshal.FreeBSTR(xml);
            Marshal.FreeBSTR(name);
            Marshal.FreeBSTR(rootName);
            Release(task);
            Release(folder);
            Release(service);
            // S_OK and S_FALSE each require a matching uninitialize. A WinUI
            // STA returns RPC_E_CHANGED_MODE and remains owned by the caller.
            if (initialized >= 0)
            {
                Ole32NativeMethods.CoUninitialize();
            }
        }
    }

    private static nint GetSlot(nint instance, int slot) => (*(nint**)instance)[slot];

    private static void Release(nint instance)
    {
        if (instance != 0)
        {
            var release = (delegate* unmanaged[Stdcall]<nint, uint>)GetSlot(instance, ReleaseSlot);
            _ = release(instance);
        }
    }

    // VARIANT's header is 8 bytes; its largest payload is two native pointers.
    // This yields the native 24-byte layout on x64/ARM64 (16 bytes on x86).
    // Only the all-zero VT_EMPTY value is passed, so no payload needs clearing.
    [StructLayout(LayoutKind.Sequential)]
    private struct EmptyVariant
    {
        private ulong _header;
        private nint _value;
        private nint _recordInfo;
    }
}
