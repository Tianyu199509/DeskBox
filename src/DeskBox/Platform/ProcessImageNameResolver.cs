using System.Runtime.InteropServices;

namespace DeskBox.Platform;

internal static partial class ProcessImageNameResolver
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    internal static unsafe string GetProcessName(uint processId)
    {
        string fullPath = GetProcessExecutablePath(processId);
        return string.IsNullOrEmpty(fullPath)
            ? string.Empty
            : Path.GetFileNameWithoutExtension(fullPath);
    }

    internal static bool IsSystemExplorerExecutablePath(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return false;
        }

        string trimmed = DriveMediaInterop.StripExtendedPathPrefix(fullPath.Trim());
        string windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(windowsDir))
        {
            return string.Equals(
                Path.GetFileName(trimmed),
                "explorer.exe",
                StringComparison.OrdinalIgnoreCase);
        }

        string expectedExplorer = Path.Combine(windowsDir, "explorer.exe");
        return string.Equals(trimmed, expectedExplorer, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsExplorerProcess(uint processId) =>
        processId != 0 && IsSystemExplorerExecutablePath(GetProcessExecutablePath(processId));

    internal static unsafe string GetProcessExecutablePath(uint processId)
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

            return new string(buffer, 0, (int)size);
        }
        finally
        {
            _ = CloseHandle(hProcess);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(
        uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
        uint dwProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageNameW(
        IntPtr hProcess,
        uint dwFlags,
        char* lpExeName,
        ref uint lpdwSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);
}
