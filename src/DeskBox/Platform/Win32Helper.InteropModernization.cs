using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DeskBox.Platform;

public static partial class Win32Helper
{
    [LibraryImport("shell32.dll", EntryPoint = "SHQueryUserNotificationState", SetLastError = false)]
    private static partial int SHQueryUserNotificationState(out int state);

    [LibraryImport("dcomp.dll", EntryPoint = "DCompositionBoostCompositorClock")]
    private static partial int DCompositionBoostCompositorClock(
        [MarshalAs(UnmanagedType.Bool)] bool enable);

    [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static partial uint TimeBeginPeriod(uint periodMilliseconds);

    [LibraryImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static partial uint TimeEndPeriod(uint periodMilliseconds);

    [LibraryImport("user32.dll", EntryPoint = "GetDesktopWindow")]
    public static partial IntPtr GetDesktopWindow();

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")]
    private static unsafe partial int GetWindowTextNative(IntPtr hWnd, char* lpString, int nMaxCount);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")]
    private static unsafe partial int GetClassNameNative(IntPtr hWnd, char* lpClassName, int nMaxCount);

    public static unsafe int GetWindowText(IntPtr hWnd, Span<char> buffer)
    {
        if (hWnd == IntPtr.Zero || buffer.IsEmpty)
        {
            return 0;
        }

        fixed (char* ptr = buffer)
        {
            return GetWindowTextNative(hWnd, ptr, buffer.Length);
        }
    }

    public static unsafe int GetClassName(IntPtr hWnd, Span<char> buffer)
    {
        if (hWnd == IntPtr.Zero || buffer.IsEmpty)
        {
            return 0;
        }

        fixed (char* ptr = buffer)
        {
            return GetClassNameNative(hWnd, ptr, buffer.Length);
        }
    }

    public static int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount)
    {
        ArgumentNullException.ThrowIfNull(lpString);
        lpString.Clear();
        if (hWnd == IntPtr.Zero || nMaxCount <= 0)
        {
            return 0;
        }

        char[]? rented = null;
        Span<char> buffer = nMaxCount <= 512
            ? stackalloc char[nMaxCount]
            : (rented = ArrayPool<char>.Shared.Rent(nMaxCount)).AsSpan(0, nMaxCount);
        try
        {
            int length = GetWindowText(hWnd, buffer);
            if (length > 0)
            {
                int safeLength = Math.Min(length, buffer.Length);
                lpString.Append(buffer[..safeLength]);
                return safeLength;
            }

            return 0;
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<char>.Shared.Return(rented);
            }
        }
    }

    public static int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount)
    {
        ArgumentNullException.ThrowIfNull(lpClassName);
        lpClassName.Clear();
        if (hWnd == IntPtr.Zero || nMaxCount <= 0)
        {
            return 0;
        }

        char[]? rented = null;
        Span<char> buffer = nMaxCount <= 256
            ? stackalloc char[nMaxCount]
            : (rented = ArrayPool<char>.Shared.Rent(nMaxCount)).AsSpan(0, nMaxCount);
        try
        {
            int length = GetClassName(hWnd, buffer);
            if (length > 0)
            {
                int safeLength = Math.Min(length, buffer.Length);
                lpClassName.Append(buffer[..safeLength]);
                return safeLength;
            }

            return 0;
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<char>.Shared.Return(rented);
            }
        }
    }

    public static unsafe uint DragQueryFile(IntPtr hDrop, uint fileIndex, StringBuilder? fileName, uint bufferSize)
    {
        if (fileName is null || bufferSize == 0)
        {
            return ClipboardNativeMethods.DragQueryFileNative(hDrop, fileIndex, null, 0);
        }

        fileName.Clear();
        int capacity = checked((int)Math.Min(bufferSize, (uint)int.MaxValue));
        char[]? rented = null;
        Span<char> buffer = capacity <= 512
            ? stackalloc char[capacity]
            : (rented = ArrayPool<char>.Shared.Rent(capacity)).AsSpan(0, capacity);
        try
        {
            fixed (char* ptr = buffer)
            {
                uint copied = ClipboardNativeMethods.DragQueryFileNative(hDrop, fileIndex, ptr, (uint)capacity);
                if (copied > 0)
                {
                    int safeCopied = (int)Math.Min(copied, (uint)buffer.Length);
                    fileName.Append(buffer[..safeCopied]);
                }

                return copied;
            }
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<char>.Shared.Return(rented);
            }
        }
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHOpenWithDialog")]
    private static unsafe partial int SHOpenWithDialog(IntPtr hwndParent, ref OpenAsInfo openAsInfo);

    [LibraryImport("shlwapi.dll", EntryPoint = "SHLoadIndirectString", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int SHLoadIndirectString(
        string source,
        char* outputBuffer,
        uint outputBufferLength,
        IntPtr reserved);

    [LibraryImport("shell32.dll", EntryPoint = "SHGetKnownFolderPath")]
    private static partial int SHGetKnownFolderPath(in Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    [LibraryImport("shell32.dll", EntryPoint = "SHObjectProperties",
        SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SHObjectProperties(
        IntPtr hwnd,
        uint shopType,
        string objectName,
        string? propertyPageName);

    [LibraryImport("comctl32.dll", EntryPoint = "SetWindowSubclass", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowSubclass(
        IntPtr hWnd,
        SubclassProc subclassProc,
        UIntPtr uIdSubclass,
        UIntPtr dwRefData);

    [LibraryImport("comctl32.dll", EntryPoint = "RemoveWindowSubclass", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RemoveWindowSubclass(
        IntPtr hWnd,
        SubclassProc subclassProc,
        UIntPtr uIdSubclass);

    [LibraryImport("comctl32.dll", EntryPoint = "DefSubclassProc", SetLastError = true)]
    public static partial IntPtr DefSubclassProc(
        IntPtr hWnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam);

    [LibraryImport("kernel32.dll", EntryPoint = "GetCurrentProcess")]
    private static partial IntPtr GetCurrentProcess();

    [LibraryImport("kernel32.dll", EntryPoint = "GetNamedPipeClientProcessId", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(
        IntPtr hNamedPipe,
        out uint lpClientProcessId);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW",
        SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string stringSecurityDescriptor,
        uint stringSecurityDescriptorRevision,
        out IntPtr securityDescriptor,
        IntPtr securityDescriptorSize);

    [LibraryImport("kernel32.dll", EntryPoint = "LocalFree")]
    private static partial IntPtr LocalFree(IntPtr hMem);

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public uint Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateNamedPipeW",
        SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafePipeHandle CreateNamedPipeW(
        string pipeName,
        uint openMode,
        uint pipeMode,
        uint maxInstances,
        uint outBufferSize,
        uint inBufferSize,
        uint defaultTimeOut,
        ref SecurityAttributes securityAttributes);

    [LibraryImport("kernel32.dll", EntryPoint = "SetProcessWorkingSetSize", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessWorkingSetSize(
        IntPtr hProcess,
        IntPtr dwMinWorkingSetSize,
        IntPtr dwMaxWorkingSetSize);

    [LibraryImport("kernel32.dll", EntryPoint = "TerminateProcess", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [LibraryImport("kernel32.dll", EntryPoint = "SetProcessInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessMemoryPriorityInformation(
        IntPtr hProcess,
        int processInformationClass,
        ref MemoryPriorityInformation processInformation,
        uint processInformationSize);

    [LibraryImport("kernel32.dll", EntryPoint = "HeapSetInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool HeapSetInformationNative(
        IntPtr heapHandle,
        int heapInformationClass,
        HEAP_OPTIMIZE_RESOURCES_INFORMATION* heapInformation,
        UIntPtr heapInformationLength);

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct MONITORINFOEX
    {
        internal static int SizeInBytes => sizeof(MONITORINFOEX);

        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        private fixed ushort _szDevice[32];

        public readonly string GetDeviceName()
        {
            fixed (ushort* ptr = _szDevice)
            {
                ReadOnlySpan<char> chars = new(ptr, 32);
                int nullIndex = chars.IndexOf('\0');
                ReadOnlySpan<char> slice = nullIndex >= 0 ? chars[..nullIndex] : chars;
                return slice.IsEmpty ? string.Empty : new string(slice);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct DEVMODE
    {
        internal static ushort SizeInBytes => (ushort)sizeof(DEVMODE);

        private fixed ushort _dmDeviceName[32];
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        private fixed ushort _dmFormName[32];
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct DISPLAY_DEVICEW
    {
        internal static int SizeInBytes => sizeof(DISPLAY_DEVICEW);

        public int cb;
        private fixed ushort _deviceName[32];
        private fixed ushort _deviceString[128];
        public uint StateFlags;
        private fixed ushort _deviceId[128];
        private fixed ushort _deviceKey[128];

        public readonly string GetDeviceString()
        {
            fixed (ushort* ptr = _deviceString)
            {
                return SliceNullTerminated(ptr, 128);
            }
        }

        public readonly string GetDeviceId()
        {
            fixed (ushort* ptr = _deviceId)
            {
                return SliceNullTerminated(ptr, 128);
            }
        }

        private static string SliceNullTerminated(ushort* ptr, int maxLength)
        {
            ReadOnlySpan<char> chars = new(ptr, maxLength);
            int nullIndex = chars.IndexOf('\0');
            ReadOnlySpan<char> slice = nullIndex >= 0 ? chars[..nullIndex] : chars;
            return slice.IsEmpty ? string.Empty : new string(slice);
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "EnumDisplayDevicesW",
        SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDisplayDevicesW(
        string? lpDevice,
        uint iDevNum,
        ref DISPLAY_DEVICEW lpDisplayDevice,
        uint dwFlags);

    [LibraryImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDisplaySettings(
        string lpszDeviceName,
        int iModeNum,
        ref DEVMODE lpDevMode);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoEx(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct OpenAsInfo
    {
        public char* File;
        public char* Class;
        public OpenAsInfoFlags Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessMemoryCountersEx
    {
        public uint cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool K32GetProcessMemoryInfo(
        IntPtr process,
        out ProcessMemoryCountersEx ppsmemCounters,
        uint cb);

    /// <summary>
    /// Queries the current process working-set and private commit bytes in O(1)
    /// via K32GetProcessMemoryInfo (~2 microseconds, zero heap allocations),
    /// avoiding Process.GetCurrentProcess().Refresh() which scans the entire OS
    /// process/thread table via NtQuerySystemInformation (~10-20 ms on UI thread).
    /// </summary>
    internal static unsafe bool TryGetCurrentProcessMemoryBytes(
        out long workingSetBytes,
        out long privateBytes)
    {
        var counters = new ProcessMemoryCountersEx
        {
            cb = (uint)sizeof(ProcessMemoryCountersEx)
        };
        if (K32GetProcessMemoryInfo(GetCurrentProcess(), out counters, counters.cb))
        {
            workingSetBytes = (long)counters.WorkingSetSize;
            privateBytes = (long)(counters.PrivateUsage != 0 ? counters.PrivateUsage : counters.PagefileUsage);
            return true;
        }

        workingSetBytes = 0;
        privateBytes = 0;
        return false;
    }
}
