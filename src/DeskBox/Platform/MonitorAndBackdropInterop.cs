using System.Runtime.InteropServices;

namespace DeskBox.Platform;

/// <summary>
/// Win32, DWM, GDI, and CoreMessaging interop declarations used by
/// <see cref="DeskBox.Services.MonitorOcclusionTracker"/>,
/// <see cref="DeskBox.Services.SolidTintSystemBackdrop"/>, and
/// <see cref="DeskBox.Views.StackPopoverHostWindow"/>.
/// </summary>
internal static unsafe class MonitorAndBackdropInterop
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeMonitorInfo
    {
        public uint CbSize;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DispatcherQueueOptions
    {
        public int dwSize;
        public int threadType;
        public int apartmentType;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct DwmBlurBehind
    {
        public uint dwFlags;
        public int fEnable;
        public IntPtr hRgnBlur;
        public int fTransitionOnMaximized;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, int, int, uint, uint, void> lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern int UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern int EnumWindows(
        delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int> lpEnumFunc,
        IntPtr lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern int IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern int IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern int IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern int GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern int GetMonitorInfoW(IntPtr hMonitor, NativeMonitorInfo* lpmi);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern int GetClassNameW(IntPtr hWnd, char* lpClassName, int nMaxCount);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    internal static extern int DwmGetWindowAttribute(
        IntPtr hwnd,
        int dwAttribute,
        void* pvAttribute,
        int cbAttribute);

    [DllImport("CoreMessaging.dll", ExactSpelling = true)]
    internal static extern int CreateDispatcherQueueController(
        DispatcherQueueOptions options,
        out IntPtr dispatcherQueueController);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    internal static extern int DwmEnableBlurBehindWindow(
        IntPtr hwnd,
        ref DwmBlurBehind blurBehind);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    internal static extern IntPtr CreateRectRgn(int x1, int y1, int x2, int y2);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    internal static extern int DeleteObject(IntPtr hObject);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern int ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern int SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern IntPtr SetActiveWindow(IntPtr hWnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
}
