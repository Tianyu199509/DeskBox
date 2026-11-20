using DeskBox.Platform;
using System.Runtime.InteropServices;

namespace DeskBox.Services;

/// <summary>
/// Direct Task Scheduler 2.0 COM client (read, register, delete) using UTF-16
/// BSTRs and raw vtable slots. Eliminates child <c>schtasks.exe</c> spawns,
/// temporary XML files, and console code-page transcoding while remaining
/// identical in JIT and Native AOT builds.
/// </summary>
internal static unsafe partial class DirectStartupTaskXmlReader
{
    // Verified against Windows SDK taskschd.h (IUnknown 0-2, IDispatch 3-6):
    //   ITaskService:    GetFolder=7, Connect=10
    //   ITaskFolder:     GetTask=13, DeleteTask=15, RegisterTask=16
    //   IRegisteredTask: get_Xml=20
    private const int ReleaseSlot = 2;
    private const int GetFolderSlot = 7;
    private const int ConnectSlot = 10;
    private const int GetTaskSlot = 13;
    private const int DeleteTaskSlot = 15;
    private const int RegisterTaskSlot = 16;
    private const int GetXmlSlot = 20;

    private const int TaskCreateOrUpdate = 6; // TASK_CREATE (2) | TASK_UPDATE (4)
    private const int TaskLogonInteractiveToken = 3; // TASK_LOGON_INTERACTIVE_TOKEN
    private const int RpcChangedMode = unchecked((int)0x80010106);
    internal const int ErrorFileNotFoundHr = unchecked((int)0x80070002);
    internal const int ErrorPathNotFoundHr = unchecked((int)0x80070003);

    private static readonly Guid TaskSchedulerClassId =
        new("0F87369F-A4E5-4CFC-BD3E-73E6154572DD");
    private static readonly Guid TaskServiceInterfaceId =
        new("2FABA4C7-4DA9-4013-9697-20CC3FD40F85");

    internal static string Read(string taskName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskName);
        return WithRootFolder(folder =>
        {
            nint name = 0;
            nint task = 0;
            nint xml = 0;
            try
            {
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
                Release(task);
            }
        });
    }

    internal static void Register(string taskName, string taskXml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskName);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskXml);
        _ = WithRootFolder(folder =>
        {
            nint path = 0;
            nint xmlText = 0;
            nint registeredTask = 0;
            try
            {
                path = Marshal.StringToBSTR(taskName);
                xmlText = Marshal.StringToBSTR(taskXml);
                var registerTask = (delegate* unmanaged[Stdcall]<
                    nint,
                    nint,
                    nint,
                    int,
                    EmptyVariant,
                    EmptyVariant,
                    int,
                    EmptyVariant,
                    nint*,
                    int>)GetSlot(folder, RegisterTaskSlot);
                int hr = registerTask(
                    folder,
                    path,
                    xmlText,
                    TaskCreateOrUpdate,
                    default,
                    default,
                    TaskLogonInteractiveToken,
                    default,
                    &registeredTask);
                Marshal.ThrowExceptionForHR(hr);
                return 0;
            }
            finally
            {
                Marshal.FreeBSTR(xmlText);
                Marshal.FreeBSTR(path);
                Release(registeredTask);
            }
        });
    }

    /// <summary>
    /// Deletes the named task from the root folder. Returns <see langword="true"/>
    /// when a task existed and was deleted, <see langword="false"/> when it was
    /// already absent, and throws on real failures (such as E_ACCESSDENIED).
    /// </summary>
    internal static bool Delete(string taskName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskName);
        return WithRootFolder(folder =>
        {
            nint name = 0;
            try
            {
                name = Marshal.StringToBSTR(taskName);
                var deleteTask = (delegate* unmanaged[Stdcall]<nint, nint, int, int>)
                    GetSlot(folder, DeleteTaskSlot);
                int hr = deleteTask(folder, name, 0);
                if (hr is ErrorFileNotFoundHr or ErrorPathNotFoundHr)
                {
                    return false;
                }

                Marshal.ThrowExceptionForHR(hr);
                return true;
            }
            finally
            {
                Marshal.FreeBSTR(name);
            }
        });
    }

    private static T WithRootFolder<T>(Func<nint, T> action)
    {
        int initialized = Ole32NativeMethods.CoInitializeEx(0, 0); // COINIT_MULTITHREADED
        if (initialized < 0 && initialized != RpcChangedMode)
        {
            Marshal.ThrowExceptionForHR(initialized);
        }

        nint service = 0;
        nint folder = 0;
        nint rootName = 0;
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

            return action(folder);
        }
        finally
        {
            Marshal.FreeBSTR(rootName);
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
