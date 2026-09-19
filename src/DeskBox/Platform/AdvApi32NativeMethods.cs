using System.Runtime.InteropServices;

namespace DeskBox.Platform;

/// <summary>
/// advapi32 token-query entry points used by process-elevation checks.
/// </summary>
internal static partial class AdvApi32NativeMethods
{
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetTokenInformation(
        nint tokenHandle,
        int tokenInformationClass,
        out TokenElevation tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [StructLayout(LayoutKind.Sequential)]
    internal struct TokenElevation
    {
        public int TokenIsElevated;
    }
}
