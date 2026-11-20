using System.Runtime.InteropServices;
using DeskBox.Models;

namespace DeskBox.Platform;

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

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct DisplaySourceName
    {
        internal static uint SizeInBytes => (uint)sizeof(DisplaySourceName);

        public uint Type, Size;
        public DisplayAdapterId AdapterId;
        public uint Id;
        private fixed ushort _deviceName[32];

        public readonly string GetDeviceName()
        {
            fixed (ushort* ptr = _deviceName)
            {
                return ReadNullTerminatedUtf16(ptr, 32);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct DisplayTargetName
    {
        internal static uint SizeInBytes => (uint)sizeof(DisplayTargetName);

        public uint Type, Size;
        public DisplayAdapterId AdapterId;
        public uint Id;
        // Full native layout of DISPLAYCONFIG_TARGET_DEVICE_NAME (wingdi.h):
        // a short Size makes DisplayConfigGetDeviceInfo reject the request
        // (ERROR_INSUFFICIENT_BUFFER), silently killing friendly names.
        public uint Flags;             // DISPLAYCONFIG_TARGET_DEVICE_NAME_FLAGS
        public uint OutputTechnology;  // DISPLAYCONFIG_OUTPUT_TECHNOLOGY
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;
        private fixed ushort _monitorFriendlyDeviceName[64];
        private fixed ushort _monitorDevicePath[128];

        public readonly string GetMonitorFriendlyDeviceName()
        {
            fixed (ushort* ptr = _monitorFriendlyDeviceName)
            {
                return ReadNullTerminatedUtf16(ptr, 64);
            }
        }

        public readonly string GetMonitorDevicePath()
        {
            fixed (ushort* ptr = _monitorDevicePath)
            {
                return ReadNullTerminatedUtf16(ptr, 128);
            }
        }
    }

    internal const uint DisplayDeviceActiveFlag = 0x00000001;

    internal static string ResolveActiveMonitorStableId(string? deviceName)
    {
        string fallback = string.IsNullOrWhiteSpace(deviceName) ? "unknown-display" : deviceName.Trim();
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return fallback;
        }

        List<(uint StateFlags, string? DeviceId, string? DeviceKey)>? entries = null;
        try
        {
            for (uint deviceIndex = 0; deviceIndex < 16; deviceIndex++)
            {
                var displayDevice = new DisplayDevice
                {
                    Size = DisplayDevice.SizeInBytes,
                };
                if (!EnumDisplayDevices(
                        deviceName,
                        deviceIndex,
                        ref displayDevice,
                        EddGetDeviceInterfaceName))
                {
                    break;
                }

                string deviceId = displayDevice.GetDeviceId();
                // Fast path for normal physical monitors on Win10/Win11/Store:
                // slot 0 (or the first active slot) immediately returns its PnP
                // interface path with zero list/closure allocations or extra syscalls.
                if ((displayDevice.StateFlags & DisplayDeviceActiveFlag) != 0 &&
                    !string.IsNullOrWhiteSpace(deviceId))
                {
                    return deviceId.Trim();
                }

                entries ??= [];
                entries.Add((displayDevice.StateFlags, deviceId, displayDevice.GetDeviceKey()));
            }
        }
        catch (Exception ex) when (
            ex is DllNotFoundException or
                EntryPointNotFoundException or
                BadImageFormatException or
                MarshalDirectiveException or
                TypeLoadException)
        {
        }

        return SelectPreferredMonitorStableId(
            entries ?? [],
            GetSystemMetrics(SM_REMOTESESSION) != 0
                ? null
                : () => QueryActiveMonitorDevicePathForSource(deviceName),
            fallback);
    }

    internal static string SelectPreferredMonitorStableId(
        IReadOnlyList<(uint StateFlags, string? DeviceId, string? DeviceKey)> entries,
        Func<string?>? ccdDevicePathFallback,
        string fallback)
    {
        bool anyActive = false;
        foreach ((uint flags, string? deviceId, _) in entries)
        {
            if ((flags & DisplayDeviceActiveFlag) == 0)
            {
                continue;
            }

            anyActive = true;
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                return deviceId.Trim();
            }
        }

        string? ccdPath = ccdDevicePathFallback?.Invoke();
        if (!string.IsNullOrWhiteSpace(ccdPath))
        {
            return ccdPath.Trim();
        }

        if (anyActive)
        {
            foreach ((uint flags, _, string? deviceKey) in entries)
            {
                if ((flags & DisplayDeviceActiveFlag) != 0 && !string.IsNullOrWhiteSpace(deviceKey))
                {
                    return deviceKey.Trim();
                }
            }

            return fallback;
        }

        foreach ((_, string? deviceId, string? deviceKey) in entries)
        {
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                return deviceId.Trim();
            }

            if (!string.IsNullOrWhiteSpace(deviceKey))
            {
                return deviceKey.Trim();
            }
        }

        return fallback;
    }

    internal static string? QueryActiveMonitorDevicePathForSource(string sourceDeviceName)
    {
        if (string.IsNullOrWhiteSpace(sourceDeviceName))
        {
            return null;
        }

        // QDC_ONLY_ACTIVE_PATHS (0x02): universal across Windows 10 (19041+),
        // Windows 11, WDDM 1.x/2.x/3.x, DisplayLink/IddCx adapters, and Store
        // runFullTrust packages; modes[] are unused so QDC_VIRTUAL_MODE_AWARE is omitted.
        const uint flags = 0x02u;
        try
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                int status = GetDisplayConfigBufferSizes(flags, out uint pathCount, out uint modeCount);
                if (status != 0 || pathCount > 128 || modeCount > 512) return null;
                var paths = new DisplayPath[pathCount];
                var modes = new DisplayMode[modeCount];
                status = QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
                if (status == 122) continue;
                if (status != 0) return null;
                for (int i = 0; i < pathCount; i++)
                {
                    DisplayPath path = paths[i];
                    var source = new DisplaySourceName
                    {
                        Type = 1,
                        Size = DisplaySourceName.SizeInBytes,
                        AdapterId = path.Source.AdapterId,
                        Id = path.Source.Id,
                    };
                    if (DisplayConfigGetDeviceInfo(ref source) != 0 ||
                        !string.Equals(source.GetDeviceName().Trim(), sourceDeviceName.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var target = new DisplayTargetName
                    {
                        Type = 2,
                        Size = DisplayTargetName.SizeInBytes,
                        AdapterId = path.Target.AdapterId,
                        Id = path.Target.Id,
                    };
                    if (DisplayConfigGetDeviceInfoTarget(ref target) == 0)
                    {
                        string monitorDevicePath = target.GetMonitorDevicePath();
                        if (!string.IsNullOrWhiteSpace(monitorDevicePath))
                        {
                            return monitorDevicePath.Trim();
                        }
                    }
                }

                return null;
            }
        }
        catch (Exception ex) when (
            ex is DllNotFoundException or
                EntryPointNotFoundException or
                BadImageFormatException or
                MarshalDirectiveException or
                TypeLoadException)
        {
        }

        return null;
    }

    /// <summary>
    /// Queries the monitor friendly name (e.g. "DELL U2720Q") per device
    /// interface path via CCD (spec 4.6). The path key matches the stable
    /// monitor identity used across placement. Virtual/absent names fall
    /// back to an empty string; callers substitute their own fallbacks.
    /// </summary>
    internal static Dictionary<string, string> QueryMonitorFriendlyNames()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        uint flags = 0x02u | 0x10u;
        try
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                int status = GetDisplayConfigBufferSizes(flags, out uint pathCount, out uint modeCount);
                if (status != 0 || pathCount > 128 || modeCount > 512) return result;
                var paths = new DisplayPath[pathCount];
                var modes = new DisplayMode[modeCount];
                status = QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
                if (status == 122) continue;
                if (status != 0) return result;
                foreach (DisplayPath path in paths)
                {
                    var request = new DisplayTargetName
                    {
                        Type = 2,
                        Size = DisplayTargetName.SizeInBytes,
                        AdapterId = path.Target.AdapterId,
                        Id = path.Target.Id,
                    };
                    if (DisplayConfigGetDeviceInfoTarget(ref request) != 0) continue;
                    string name = request.GetMonitorFriendlyDeviceName().Trim();
                    string devicePath = request.GetMonitorDevicePath().Trim();
                    if (devicePath.Length > 0 && name.Length > 0)
                    {
                        result[devicePath] = name;
                    }
                }

                return result;
            }
        }
        catch (Exception ex) when (
            ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
        }

        return result;
    }

    [LibraryImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static partial int DisplayConfigGetDeviceInfoTarget(ref DisplayTargetName request);

    [LibraryImport("user32.dll", EntryPoint = "GetDisplayConfigBufferSizes")]
    private static partial int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [LibraryImport("user32.dll", EntryPoint = "QueryDisplayConfig")]
    private static partial int QueryDisplayConfig(uint flags, ref uint pathCount,
        [Out] DisplayPath[] paths, ref uint modeCount, [Out] DisplayMode[] modes, IntPtr topologyId);

    [LibraryImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static partial int DisplayConfigGetDeviceInfo(ref DisplaySourceName request);

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
                        Type = 1, Size = DisplaySourceName.SizeInBytes,
                        AdapterId = path.Source.AdapterId, Id = path.Source.Id,
                    };
                    if (DisplayConfigGetDeviceInfo(ref name) != 0) continue;
                    string sourceDevice = name.GetDeviceName();
                    if (string.IsNullOrEmpty(sourceDevice)) continue;
                    double refresh = WidgetDisplayRefreshRatePolicy.ResolveRationalRate(
                        path.Target.RefreshRate.Numerator, path.Target.RefreshRate.Denominator);
                    uint index = (path.Flags & 0x08) != 0 ? path.Target.ModeIndex >> 16 : path.Target.ModeIndex;
                    double physical = index < modeCount && modes[index].InfoType == 2
                        ? WidgetDisplayRefreshRatePolicy.ResolveRationalRate(
                            modes[index].VerticalSync.Numerator, modes[index].VerticalSync.Denominator, refresh)
                        : refresh;
                    var timing = new DisplayTiming(refresh, physical, (path.Flags & 0x10) != 0);
                    // Mirrored targets share one source: use the fastest active target as a budget.
                    if (!result.TryGetValue(sourceDevice, out var previous) ||
                        Math.Max(timing.RefreshRateHz, timing.PhysicalRefreshRateHz) >
                        Math.Max(previous.RefreshRateHz, previous.PhysicalRefreshRateHz))
                        result[sourceDevice] = timing;
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

    internal static bool TryGetCachedAnimationDisplayTiming(
        IntPtr monitor,
        IReadOnlyDictionary<string, DisplayTiming> timings,
        out DisplayTiming timing)
    {
        if (timings.Count > 0 && monitor != IntPtr.Zero)
        {
            var info = new MONITORINFOEX { cbSize = MONITORINFOEX.SizeInBytes };
            if (GetMonitorInfoEx(monitor, ref info))
            {
                string deviceName = info.GetDeviceName();
                if (!string.IsNullOrEmpty(deviceName) && timings.TryGetValue(deviceName, out timing))
                {
                    return true;
                }
            }
        }

        timing = new(WidgetDisplayRefreshRatePolicy.DefaultRefreshRateHz, WidgetDisplayRefreshRatePolicy.DefaultRefreshRateHz, false);
        return false;
    }

    internal static DisplayTiming GetAnimationDisplayTiming(IntPtr monitor, IReadOnlyDictionary<string, DisplayTiming> timings)
    {
        var info = new MONITORINFOEX { cbSize = MONITORINFOEX.SizeInBytes };
        if (monitor != IntPtr.Zero && GetMonitorInfoEx(monitor, ref info))
        {
            string deviceName = info.GetDeviceName();
            if (!string.IsNullOrEmpty(deviceName))
            {
                if (timings.TryGetValue(deviceName, out var timing)) return timing;
                var mode = new DEVMODE { dmSize = DEVMODE.SizeInBytes };
                if (EnumDisplaySettings(deviceName, EnumCurrentSettings, ref mode))
                {
                    double rate = WidgetDisplayRefreshRatePolicy.Normalize(mode.dmDisplayFrequency);
                    return new(rate, rate, false);
                }
            }
        }
        return new(60, 60, false);
    }
}
