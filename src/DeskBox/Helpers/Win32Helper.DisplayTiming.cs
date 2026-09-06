using System.Runtime.InteropServices;
using DeskBox.Models;

namespace DeskBox.Helpers;

public static partial class Win32Helper
{
    internal readonly record struct DisplayTiming(double RefreshRateHz, double PhysicalRefreshRateHz, bool IsDynamic);

    [StructLayout(LayoutKind.Sequential)]
    internal struct DisplayAdapterId { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DisplayRate { public uint Numerator; public uint Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DisplayPathSource
    {
        public DisplayAdapterId AdapterId;
        public uint Id, ModeIndex, StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DisplayPathTarget
    {
        public DisplayAdapterId AdapterId;
        public uint Id, ModeIndex, OutputTechnology, Rotation, Scaling;
        public DisplayRate RefreshRate;
        public uint ScanLineOrdering, TargetAvailable, StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DisplayPath
    {
        public DisplayPathSource Source;
        public DisplayPathTarget Target;
        public uint Flags;
    }

    // DISPLAYCONFIG_MODE_INFO contains a 48-byte native union after its header.
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    internal struct DisplayMode
    {
        [FieldOffset(0)] public uint InfoType;
        [FieldOffset(4)] public uint Id;
        [FieldOffset(8)] public DisplayAdapterId AdapterId;
        [FieldOffset(32)] public DisplayRate VerticalSync;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplaySourceName
    {
        public uint Type, Size;
        public DisplayAdapterId AdapterId;
        public uint Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount,
        [Out] DisplayPath[] paths, ref uint modeCount, [Out] DisplayMode[] modes, IntPtr topologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplaySourceName request);

    internal static Dictionary<string, DisplayTiming> QueryActiveDisplayTimings()
    {
        var result = new Dictionary<string, DisplayTiming>(StringComparer.OrdinalIgnoreCase);
        // Virtual mode indexing exists on Win10; virtual refresh rates are Win11-only.
        uint flags = 0x02u | 0x10u;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) flags |= 0x40u;
        try
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                int status = GetDisplayConfigBufferSizes(flags, out uint pathCount, out uint modeCount);
                if (status != 0 || pathCount > 128 || modeCount > 512) return result;
                var paths = new DisplayPath[pathCount];
                var modes = new DisplayMode[modeCount];
                status = QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
                if (status == 122) continue; // Topology changed between the two calls.
                if (status != 0) return result;
                for (int i = 0; i < pathCount; i++)
                {
                    DisplayPath path = paths[i];
                    var name = new DisplaySourceName
                    {
                        Type = 1, Size = (uint)Marshal.SizeOf<DisplaySourceName>(),
                        AdapterId = path.Source.AdapterId, Id = path.Source.Id, DeviceName = string.Empty
                    };
                    if (DisplayConfigGetDeviceInfo(ref name) != 0 || string.IsNullOrEmpty(name.DeviceName)) continue;
                    double refresh = WidgetDisplayRefreshRatePolicy.ResolveRationalRate(
                        path.Target.RefreshRate.Numerator, path.Target.RefreshRate.Denominator);
                    uint index = (path.Flags & 0x08) != 0 ? path.Target.ModeIndex >> 16 : path.Target.ModeIndex;
                    double physical = index < modeCount && modes[index].InfoType == 2
                        ? WidgetDisplayRefreshRatePolicy.ResolveRationalRate(
                            modes[index].VerticalSync.Numerator, modes[index].VerticalSync.Denominator, refresh)
                        : refresh;
                    var timing = new DisplayTiming(refresh, physical, (path.Flags & 0x10) != 0);
                    // Mirrored targets share one source: use the fastest active target as a budget.
                    if (!result.TryGetValue(name.DeviceName, out var previous) ||
                        Math.Max(timing.RefreshRateHz, timing.PhysicalRefreshRateHz) >
                        Math.Max(previous.RefreshRateHz, previous.PhysicalRefreshRateHz))
                        result[name.DeviceName] = timing;
                }
                return result;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            // Remote/older display stacks use the existing EnumDisplaySettings fallback.
        }
        return result;
    }

    internal static IntPtr GetAnimationMonitor(IntPtr windowHandle) => windowHandle != IntPtr.Zero
        ? MonitorFromWindow(windowHandle, MONITOR_DEFAULTTONEAREST)
        : MonitorFromPoint(default, MONITOR_DEFAULTTOPRIMARY);

    internal static IntPtr GetAnimationMonitorForPoint(int x, int y) =>
        MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONEAREST);

    internal static DisplayTiming GetAnimationDisplayTiming(IntPtr monitor, IReadOnlyDictionary<string, DisplayTiming> timings)
    {
        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>(), szDevice = string.Empty };
        if (monitor != IntPtr.Zero && GetMonitorInfoEx(monitor, ref info))
        {
            if (timings.TryGetValue(info.szDevice, out var timing)) return timing;
            var mode = new DEVMODE { dmDeviceName = string.Empty, dmFormName = string.Empty, dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            if (EnumDisplaySettings(info.szDevice, EnumCurrentSettings, ref mode))
            {
                double rate = WidgetDisplayRefreshRatePolicy.Normalize((uint)Math.Max(0, mode.dmDisplayFrequency));
                return new(rate, rate, false);
            }
        }
        return new(60, 60, false);
    }
}
