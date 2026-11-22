using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DeskBox.Platform;

internal static class CustomWidgetSystemControlInterop
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;
    private const uint PowerRequestContextVersion = 0;
    private const uint PowerRequestContextSimpleString = 0x00000001;
    private const int PowerRequestDisplayRequired = 0;
    private const int PowerRequestSystemRequired = 1;
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
    private const uint SndAsync = 0x0001;
    private const uint SndNodefault = 0x0002;
    private const uint SndMemory = 0x0004;

    private static readonly object PlaySoundSync = new();
    private static readonly object KeepAwakeSync = new();
    private static readonly object ExternalBrightnessSync = new();
    private const long ExternalBrightnessSupportedTtlMs = 60_000L;
    private const long ExternalBrightnessUnsupportedTtlMs = 300_000L;
    private static byte[]? s_activePlaySoundBuffer;
    private static IntPtr s_keepAwakePowerRequest;
    private static IntPtr s_cachedBrightnessMonitor;
    private static int s_cachedBrightnessPercent = 80;
    private static bool s_cachedBrightnessSupported;
    private static long s_cachedBrightnessExpiresTick64;

    [DllImport("winmm.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySoundW(IntPtr pszSound, IntPtr hmod, uint fdwSound);

    internal static void PlayPinnedWaveMemoryAsync(byte[] pinnedWav)
    {
        lock (PlaySoundSync)
        {
            if (s_activePlaySoundBuffer is not null)
            {
                _ = PlaySoundW(IntPtr.Zero, IntPtr.Zero, SndNodefault);
            }

            s_activePlaySoundBuffer = pinnedWav;
            IntPtr bufferPtr = Marshal.UnsafeAddrOfPinnedArrayElement(pinnedWav, 0);
            _ = PlaySoundW(bufferPtr, IntPtr.Zero, SndMemory | SndAsync | SndNodefault);
        }
    }

    internal static void StopPinnedWaveMemory()
    {
        lock (PlaySoundSync)
        {
            if (s_activePlaySoundBuffer is not null)
            {
                _ = PlaySoundW(IntPtr.Zero, IntPtr.Zero, SndNodefault);
                s_activePlaySoundBuffer = null;
            }
        }
    }

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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
    public unsafe struct PhysicalMonitor
    {
        public IntPtr hPhysicalMonitor;
        public fixed char szPhysicalMonitorDescription[128];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        public IntPtr SimpleReasonString;
        public uint Reserved1;
        public uint Reserved2;
        public IntPtr Reserved3;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint SetThreadExecutionState(uint esFlags);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr PowerCreateRequest(ref ReasonContext context);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerSetRequest(IntPtr powerRequest, int requestType);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerClearRequest(IntPtr powerRequest, int requestType);

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
    [return: MarshalAs(UnmanagedType.Bool)]
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
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr MonitorFromPoint(NativePoint pt, uint dwFlags);

    [DllImport("dxva2.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint pdwNumberOfPhysicalMonitors);

    [DllImport("dxva2.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool GetPhysicalMonitorsFromHMONITOR(
        IntPtr hMonitor,
        uint dwPhysicalMonitorArraySize,
        PhysicalMonitor* pPhysicalMonitorArray);

    [DllImport("dxva2.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorBrightness(
        IntPtr hMonitor,
        out uint pdwMinimumBrightness,
        out uint pdwCurrentBrightness,
        out uint pdwMaximumBrightness);

    [DllImport("dxva2.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetMonitorBrightness(IntPtr hMonitor, uint dwNewBrightness);

    [DllImport("dxva2.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool DestroyPhysicalMonitors(
        uint dwPhysicalMonitorArraySize,
        PhysicalMonitor* pPhysicalMonitorArray);

    internal static void SetKeepAwakeExecutionState(bool enable)
    {
        lock (KeepAwakeSync)
        {
            if (enable)
            {
                if (s_keepAwakePowerRequest == IntPtr.Zero || s_keepAwakePowerRequest == new IntPtr(-1))
                {
                    IntPtr reasonPtr = Marshal.StringToHGlobalUni("DeskBox Keep Awake");
                    try
                    {
                        var context = new ReasonContext
                        {
                            Version = PowerRequestContextVersion,
                            Flags = PowerRequestContextSimpleString,
                            SimpleReasonString = reasonPtr
                        };
                        s_keepAwakePowerRequest = PowerCreateRequest(ref context);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(reasonPtr);
                    }
                }

                if (s_keepAwakePowerRequest != IntPtr.Zero && s_keepAwakePowerRequest != new IntPtr(-1))
                {
                    _ = PowerSetRequest(s_keepAwakePowerRequest, PowerRequestSystemRequired);
                    _ = PowerSetRequest(s_keepAwakePowerRequest, PowerRequestDisplayRequired);
                    return;
                }
            }
            else if (s_keepAwakePowerRequest != IntPtr.Zero && s_keepAwakePowerRequest != new IntPtr(-1))
            {
                _ = PowerClearRequest(s_keepAwakePowerRequest, PowerRequestDisplayRequired);
                _ = PowerClearRequest(s_keepAwakePowerRequest, PowerRequestSystemRequired);
                return;
            }
        }

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

    internal static bool TryGetDisplayBrightnessPercent(out int percent, bool bypassCache = false)
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

        // 2. External DDC/CI physical monitor via dxva2 (cached to avoid 50-100ms I2C bus stalls)
        IntPtr hMon = IntPtr.Zero;
        try
        {
            hMon = MonitorFromPoint(new NativePoint { X = 0, Y = 0 }, MonitorDefaultToPrimary);
            if (hMon == IntPtr.Zero)
            {
                return false;
            }

            long nowTick = Environment.TickCount64;
            if (!bypassCache)
            {
                lock (ExternalBrightnessSync)
                {
                    if (s_cachedBrightnessMonitor == hMon && nowTick < s_cachedBrightnessExpiresTick64)
                    {
                        percent = s_cachedBrightnessPercent;
                        return s_cachedBrightnessSupported;
                    }
                }
            }

            bool supported = false;
            int queriedPercent = 80;
            if (GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out uint count) &&
                count > 0 && count <= 8)
            {
                unsafe
                {
                    PhysicalMonitor* pMonitors = stackalloc PhysicalMonitor[(int)count];
                    if (GetPhysicalMonitorsFromHMONITOR(hMon, count, pMonitors))
                    {
                        try
                        {
                            if (GetMonitorBrightness(pMonitors[0].hPhysicalMonitor, out uint minB, out uint curB, out uint maxB) &&
                                maxB > minB)
                            {
                                double normalized = (curB - minB) * 100.0 / (maxB - minB);
                                queriedPercent = (int)Math.Clamp(Math.Round(normalized), 0, 100);
                                supported = true;
                            }
                        }
                        finally
                        {
                            _ = DestroyPhysicalMonitors(count, pMonitors);
                        }
                    }
                }
            }

            long ttlMs = supported ? ExternalBrightnessSupportedTtlMs : ExternalBrightnessUnsupportedTtlMs;
            lock (ExternalBrightnessSync)
            {
                s_cachedBrightnessMonitor = hMon;
                s_cachedBrightnessSupported = supported;
                if (supported)
                {
                    s_cachedBrightnessPercent = queriedPercent;
                }
                s_cachedBrightnessExpiresTick64 = Environment.TickCount64 + ttlMs;
                percent = s_cachedBrightnessPercent;
            }

            return supported;
        }
        catch
        {
            if (hMon != IntPtr.Zero)
            {
                lock (ExternalBrightnessSync)
                {
                    s_cachedBrightnessMonitor = hMon;
                    s_cachedBrightnessSupported = false;
                    s_cachedBrightnessExpiresTick64 = Environment.TickCount64 + ExternalBrightnessUnsupportedTtlMs;
                    percent = s_cachedBrightnessPercent;
                }
            }
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
                unsafe
                {
                    PhysicalMonitor* pMonitors = stackalloc PhysicalMonitor[(int)count];
                    if (GetPhysicalMonitorsFromHMONITOR(hMon, count, pMonitors))
                    {
                        try
                        {
                            if (GetMonitorBrightness(pMonitors[0].hPhysicalMonitor, out uint minB, out _, out uint maxB) &&
                                maxB > minB)
                            {
                                uint mapped = (uint)Math.Clamp(
                                    Math.Round(minB + ((maxB - minB) * (clamped / 100.0))),
                                    minB,
                                    maxB);
                                if (SetMonitorBrightness(pMonitors[0].hPhysicalMonitor, mapped))
                                {
                                    CacheExternalBrightnessWriteThrough(hMon, clamped);
                                    return true;
                                }
                            }
                        }
                        finally
                        {
                            _ = DestroyPhysicalMonitors(count, pMonitors);
                        }
                    }
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private static void CacheExternalBrightnessWriteThrough(IntPtr hMonitor, int clampedPercent)
    {
        lock (ExternalBrightnessSync)
        {
            s_cachedBrightnessMonitor = hMonitor;
            s_cachedBrightnessPercent = clampedPercent;
            s_cachedBrightnessSupported = true;
            s_cachedBrightnessExpiresTick64 = Environment.TickCount64 + ExternalBrightnessSupportedTtlMs;
        }
    }
}
