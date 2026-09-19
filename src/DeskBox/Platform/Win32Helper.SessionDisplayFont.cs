using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DeskBox.Platform;

/// <summary>
/// Session, display, and font interop for the desktop-environment consumers
/// that used to declare these entry points privately (lifecycle session
/// watcher, topology monitor identity, organizer work-area/DPI, font catalog).
/// </summary>
public static partial class Win32Helper
{
    // ── wtsapi32: session change notifications ──────────────────────

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WTSRegisterSessionNotification(IntPtr hWnd, uint flags);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);

    // ── user32: display device identity ─────────────────────────────

    [DllImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplayDevices(
        string device,
        uint deviceIndex,
        ref DisplayDevice displayDevice,
        uint flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DisplayDevice
    {
        public int Size;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;

        public uint StateFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    // ── user32: work area and system DPI ────────────────────────────

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SystemParametersInfo(
        uint action,
        uint parameter,
        ref NativeRect value,
        uint update);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForSystem();

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    // ── gdi32: installed font family enumeration ────────────────────

    [DllImport("gdi32.dll", ExactSpelling = true)]
    internal static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteDC(IntPtr deviceContext);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int EnumFontFamiliesExW(
        IntPtr deviceContext,
        ref LogFont logFont,
        EnumFontFamilyDelegate callback,
        IntPtr parameter,
        uint flags);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate int EnumFontFamilyDelegate(
        IntPtr fontInfo,
        IntPtr textMetrics,
        uint fontType,
        IntPtr parameter);

    [StructLayout(LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    internal struct LogFont
    {
        public int Height;
        public int Width;
        public int Escapement;
        public int Orientation;
        public int Weight;
        public byte Italic;
        public byte Underline;
        public byte StrikeOut;
        public byte CharSet;
        public byte OutPrecision;
        public byte ClipPrecision;
        public byte Quality;
        public byte PitchAndFamily;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string FaceName;
    }
}
