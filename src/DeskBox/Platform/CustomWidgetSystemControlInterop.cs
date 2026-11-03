using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DeskBox.Platform;

internal static class CustomWidgetSystemControlInterop
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;
    private const uint KeyEventfKeyUp = 0x0002;

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;

    // Video miniport brightness IOCTLs for internal/laptop displays (\\.\LCD)
    private const uint IoctlVideoQueryDisplayBrightness = 0x00230498;
    private const uint IoctlVideoSetDisplayBrightness = 0x0023049C;
    private const byte DisplayPolicyBoth = 0x03;

    private const uint MonitorDefaultToPrimary = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayBrightness
    {
        public byte ucDisplayPolicy;
        public byte ucACBrightness;
        public byte ucDCBrightness;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PhysicalMonitor
    {
        public IntPtr hPhysicalMonitor;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint SetThreadExecutionState(uint esFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        ref DisplayBrightness lpInBuffer,
        uint nInBufferSize,
        ref DisplayBrightness lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool LockWorkStation();

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr MonitorFromPoint(NativePoint pt, uint dwFlags);

    [DllImport("dxva2.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint pdwNumberOfPhysicalMonitors);

    [DllImport("dxva2.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(
        IntPtr hMonitor,
        uint dwPhysicalMonitorArraySize,
        [Out] PhysicalMonitor[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool GetMonitorBrightness(
        IntPtr hMonitor,
        out uint pdwMinimumBrightness,
        out uint pdwCurrentBrightness,
        out uint pdwMaximumBrightness);

    [DllImport("dxva2.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool SetMonitorBrightness(IntPtr hMonitor, uint dwNewBrightness);

    [DllImport("dxva2.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool DestroyPhysicalMonitors(
        uint dwPhysicalMonitorArraySize,
        [In] PhysicalMonitor[] pPhysicalMonitorArray);

    internal static void SetKeepAwakeExecutionState(bool enable)
    {
        uint flags = enable
            ? EsContinuous | EsSystemRequired | EsDisplayRequired
            : EsContinuous;
        _ = SetThreadExecutionState(flags);
    }

    internal static void LockDesktopWorkstation()
    {
        _ = LockWorkStation();
    }

    internal static void SendVirtualKeyTap(byte virtualKey)
    {
        keybd_event(virtualKey, 0, 0, UIntPtr.Zero);
        keybd_event(virtualKey, 0, KeyEventfKeyUp, UIntPtr.Zero);
    }

    internal static bool TryGetDisplayBrightnessPercent(out int percent)
    {
        percent = 80;

        // 1. Internal panel via \\.\LCD IOCTL (instant, zero COM/WMI overhead)
        try
        {
            using SafeFileHandle lcd = CreateFileW(
                @"\\.\LCD",
                GenericRead,
                FileShareRead | FileShareWrite,
                IntPtr.Zero,
                OpenExisting,
                0,
                IntPtr.Zero);
            if (!lcd.IsInvalid)
            {
                DisplayBrightness dummy = default;
                DisplayBrightness outBuf = default;
                if (DeviceIoControl(
                        lcd,
                        IoctlVideoQueryDisplayBrightness,
                        ref dummy,
                        0,
                        ref outBuf,
                        (uint)Marshal.SizeOf<DisplayBrightness>(),
                        out uint returned,
                        IntPtr.Zero) &&
                    returned >= 3)
                {
                    int val = outBuf.ucDCBrightness > 0 ? outBuf.ucDCBrightness : outBuf.ucACBrightness;
                    percent = Math.Clamp(val, 0, 100);
                    return true;
                }
            }
        }
        catch
        {
        }

        // 2. External DDC/CI physical monitor via dxva2
        try
        {
            IntPtr hMon = MonitorFromPoint(new NativePoint { X = 0, Y = 0 }, MonitorDefaultToPrimary);
            if (hMon != IntPtr.Zero &&
                GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out uint count) &&
                count > 0 && count <= 8)
            {
                var monitors = new PhysicalMonitor[count];
                if (GetPhysicalMonitorsFromHMONITOR(hMon, count, monitors))
                {
                    try
                    {
                        if (GetMonitorBrightness(monitors[0].hPhysicalMonitor, out uint minB, out uint curB, out uint maxB) &&
                            maxB > minB)
                        {
                            double normalized = (curB - minB) * 100.0 / (maxB - minB);
                            percent = (int)Math.Clamp(Math.Round(normalized), 0, 100);
                            return true;
                        }
                    }
                    finally
                    {
                        _ = DestroyPhysicalMonitors(count, monitors);
                    }
                }
            }
        }
        catch
        {
        }

        return false;
    }

    internal static bool TrySetDisplayBrightnessPercent(int targetPercent)
    {
        int clamped = Math.Clamp(targetPercent, 0, 100);

        // 1. Internal panel via \\.\LCD IOCTL
        try
        {
            using SafeFileHandle lcd = CreateFileW(
                @"\\.\LCD",
                GenericRead | GenericWrite,
                FileShareRead | FileShareWrite,
                IntPtr.Zero,
                OpenExisting,
                0,
                IntPtr.Zero);
            if (!lcd.IsInvalid)
            {
                var inBuf = new DisplayBrightness
                {
                    ucDisplayPolicy = DisplayPolicyBoth,
                    ucACBrightness = (byte)clamped,
                    ucDCBrightness = (byte)clamped
                };
                DisplayBrightness dummy = default;
                if (DeviceIoControl(
                        lcd,
                        IoctlVideoSetDisplayBrightness,
                        ref inBuf,
                        (uint)Marshal.SizeOf<DisplayBrightness>(),
                        ref dummy,
                        0,
                        out _,
                        IntPtr.Zero))
                {
                    return true;
                }
            }
        }
        catch
        {
        }

        // 2. External DDC/CI physical monitor via dxva2
        try
        {
            IntPtr hMon = MonitorFromPoint(new NativePoint { X = 0, Y = 0 }, MonitorDefaultToPrimary);
            if (hMon != IntPtr.Zero &&
                GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out uint count) &&
                count > 0 && count <= 8)
            {
                var monitors = new PhysicalMonitor[count];
                if (GetPhysicalMonitorsFromHMONITOR(hMon, count, monitors))
                {
                    try
                    {
                        if (GetMonitorBrightness(monitors[0].hPhysicalMonitor, out uint minB, out _, out uint maxB) &&
                            maxB > minB)
                        {
                            uint mapped = (uint)Math.Clamp(
                                Math.Round(minB + ((maxB - minB) * (clamped / 100.0))),
                                minB,
                                maxB);
                            if (SetMonitorBrightness(monitors[0].hPhysicalMonitor, mapped))
                            {
                                return true;
                            }
                        }
                    }
                    finally
                    {
                        _ = DestroyPhysicalMonitors(count, monitors);
                    }
                }
            }
        }
        catch
        {
        }

        return false;
    }
}
