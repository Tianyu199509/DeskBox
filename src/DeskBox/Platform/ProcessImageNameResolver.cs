using System.Runtime.InteropServices;

namespace DeskBox.Platform;

internal static class ProcessImageNameResolver
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    internal static unsafe string GetProcessName(uint processId)
    {
        if (processId == 0)
        {
            return string.Empty;
        }

        IntPtr hProcess = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (hProcess == IntPtr.Zero)
        {
            return string.Empty;
        }

        try
        {
            char* buffer = stackalloc char[520];
            uint size = 520;
            if (!QueryFullProcessImageNameW(hProcess, 0, buffer, ref size) || size == 0)
            {
                return string.Empty;
            }

            ReadOnlySpan<char> fullPath = new(buffer, (int)size);
            ReadOnlySpan<char> fileName = Path.GetFileNameWithoutExtension(fullPath);
            return fileName.IsEmpty ? string.Empty : fileName.ToString();
        }
        finally
        {
            _ = CloseHandle(hProcess);
        }
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr OpenProcess(
        uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
        uint dwProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool QueryFullProcessImageNameW(
        IntPtr hProcess,
        uint dwFlags,
        char* lpExeName,
        ref uint lpdwSize);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
