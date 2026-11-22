using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DeskBox.Platform;

/// <summary>
/// Session, display, and font interop for the desktop-environment consumers
/// that used to declare these entry points privately (lifecycle session
/// watcher, topology monitor identity, organizer work-area/DPI, font catalog).
/// </summary>
public static partial class Win32Helper
{
    // ── wtsapi32: session change notifications ──────────────────────

    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSRegisterSessionNotification", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WTSRegisterSessionNotification(IntPtr hWnd, uint flags);

    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSUnRegisterSessionNotification", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WTSUnRegisterSessionNotification(IntPtr hWnd);

    // ── user32: display device identity ─────────────────────────────

    [LibraryImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumDisplayDevices(
        string device,
        uint deviceIndex,
        ref DisplayDevice displayDevice,
        uint flags);

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct DisplayDevice
    {
        internal static int SizeInBytes => sizeof(DisplayDevice);

        public int Size;
        private fixed ushort _deviceName[32];
        private fixed ushort _deviceString[128];
        public uint StateFlags;
        private fixed ushort _deviceId[128];
        private fixed ushort _deviceKey[128];

        public readonly string GetDeviceId()
        {
            fixed (ushort* ptr = _deviceId)
            {
                return ReadNullTerminatedUtf16(ptr, 128);
            }
        }

        public readonly string GetDeviceKey()
        {
            fixed (ushort* ptr = _deviceKey)
            {
                return ReadNullTerminatedUtf16(ptr, 128);
            }
        }
    }

    // ── user32: work area and system DPI ────────────────────────────

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SystemParametersInfo(
        uint action,
        uint parameter,
        ref NativeRect value,
        uint update);

    [LibraryImport("user32.dll", EntryPoint = "GetDpiForSystem")]
    internal static partial uint GetDpiForSystem();

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    // ── gdi32: installed font family enumeration ────────────────────

    [LibraryImport("gdi32.dll", EntryPoint = "CreateCompatibleDC")]
    internal static partial IntPtr CreateCompatibleDC(IntPtr deviceContext);

    [LibraryImport("gdi32.dll", EntryPoint = "DeleteDC")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteDC(IntPtr deviceContext);

    [LibraryImport("gdi32.dll", EntryPoint = "EnumFontFamiliesExW")]
    internal static unsafe partial int EnumFontFamiliesExW(
        IntPtr deviceContext,
        ref LogFont logFont,
        delegate* unmanaged[Stdcall]<LogFont*, IntPtr, uint, IntPtr, int> callback,
        IntPtr parameter,
        uint flags);

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct LogFont
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
        private fixed ushort _faceName[32];

        public readonly string GetFaceName()
        {
            fixed (ushort* ptr = _faceName)
            {
                return ReadNullTerminatedUtf16(ptr, 32);
            }
        }
    }

    internal static unsafe string ReadNullTerminatedUtf16(ushort* ptr, int maxChars)
    {
        ReadOnlySpan<char> chars = new(ptr, maxChars);
        int nullIndex = chars.IndexOf('\0');
        ReadOnlySpan<char> slice = nullIndex >= 0 ? chars[..nullIndex] : chars;
        return slice.IsEmpty ? string.Empty : new string(slice);
    }
}
