using System.Runtime.InteropServices;

namespace DeskBox.Platform;

/// <summary>
/// kernel32/advapi32 entry points used by the QuickLook preview elevation
/// check that used to declare them privately. Signatures moved verbatim
/// (including the fully-qualified attribute form).
/// </summary>
internal static partial class QuickLookElevationNativeMethods
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    internal struct TOKEN_ELEVATION
    {
        public uint TokenIsElevated;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(
        uint dwDesiredAccess,
        bool bInheritHandle,
        int dwProcessId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr hObject);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool OpenProcessToken(
        IntPtr ProcessHandle,
        uint DesiredAccess,
        out IntPtr TokenHandle);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool GetTokenInformation(
        IntPtr TokenHandle,
        int TokenInformationClass,
        out TOKEN_ELEVATION TokenInformation,
        uint TokenInformationLength,
        out uint ReturnLength);
}
