using System.Runtime.InteropServices;

namespace DeskBox.Platform;

/// <summary>
/// kernel32/advapi32/user32 entry points used by the drag-drop permission
/// diagnostics and the explorer-user relaunch that used to declare them
/// privately. Signatures moved verbatim.
/// </summary>
internal static partial class DragDropPermissionNativeMethods
{
    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", EntryPoint = "GetExitCodeProcess", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", EntryPoint = "WaitForSingleObject", SetLastError = true)]
    internal static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        TokenInformationClass tokenInformationClass,
        IntPtr tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("advapi32.dll")]
    internal static extern IntPtr GetSidSubAuthority(IntPtr sid, uint subAuthority);

    [DllImport("advapi32.dll")]
    internal static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool InitializeProcThreadAttributeList(
        IntPtr lpAttributeList,
        int dwAttributeCount,
        int dwFlags,
        ref int lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList,
        uint dwFlags,
        IntPtr attribute,
        ref IntPtr lpValue,
        IntPtr cbSize,
        IntPtr lpPreviousValue,
        IntPtr lpReturnSize);

    [DllImport("kernel32.dll")]
    internal static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateProcess(
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref StartupInfo lpStartupInfo,
        out ProcessInformation lpProcessInformation);

    internal enum TokenInformationClass
    {
        TokenElevation = 20,
        TokenIntegrityLevel = 25
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TokenElevation
    {
        public int TokenIsElevated;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TokenMandatoryLabel
    {
        public SidAndAttributes Label;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SidAndAttributes
    {
        public IntPtr Sid;
        public int Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    internal enum CrossIntegrityProcessWaitOutcome
    {
        AlreadyExited,
        ExitedWithinTimeout,
        TimedOut,
        OpenFailed
    }

    private const uint ProcessSynchronize = 0x0010_0000;
    private const uint ProcessQueryLimitedInformation = 0x0000_1000;
    private const uint WaitObject0 = 0x0000_0000;
    private const uint WaitTimeout = 0x0000_0102;
    private const uint StillActiveExitCode = 259;
    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidParameter = 87;

    /// <summary>
    /// Waits for <paramref name="processId"/> to exit across integrity levels.
    /// A Medium-integrity child cannot open its High-integrity parent with
    /// SYNCHRONIZE (default High-IL kernel DACL + NW mandatory label return
    /// ERROR_ACCESS_DENIED), which makes <see cref="System.Diagnostics.Process.WaitForExit(int)"/>
    /// throw immediately. Try SYNCHRONIZE first for zero-latency wake when
    /// permitted, then fall back to PROCESS_QUERY_LIMITED_INFORMATION +
    /// GetExitCodeProcess bounded polling across the elevation boundary.
    /// </summary>
    internal static CrossIntegrityProcessWaitOutcome WaitForProcessExitLimited(
        int processId,
        int timeoutMs,
        out int lastWin32Error)
    {
        lastWin32Error = 0;
        if (processId <= 0)
        {
            return CrossIntegrityProcessWaitOutcome.AlreadyExited;
        }

        int clampedTimeoutMs = Math.Max(0, timeoutMs);
        IntPtr syncHandle = OpenProcess(
            ProcessSynchronize | ProcessQueryLimitedInformation,
            false,
            (uint)processId);
        if (syncHandle != IntPtr.Zero)
        {
            try
            {
                if (GetExitCodeProcess(syncHandle, out uint initialCode) &&
                    initialCode != StillActiveExitCode)
                {
                    return CrossIntegrityProcessWaitOutcome.AlreadyExited;
                }

                uint waitResult = WaitForSingleObject(syncHandle, (uint)clampedTimeoutMs);
                if (waitResult == WaitObject0)
                {
                    return CrossIntegrityProcessWaitOutcome.ExitedWithinTimeout;
                }

                if (waitResult == WaitTimeout)
                {
                    return CrossIntegrityProcessWaitOutcome.TimedOut;
                }

                lastWin32Error = Marshal.GetLastWin32Error();
                return CrossIntegrityProcessWaitOutcome.OpenFailed;
            }
            finally
            {
                _ = CloseHandle(syncHandle);
            }
        }

        int syncOpenError = Marshal.GetLastWin32Error();
        if (syncOpenError == ErrorInvalidParameter)
        {
            return CrossIntegrityProcessWaitOutcome.AlreadyExited;
        }

        if (syncOpenError != ErrorAccessDenied)
        {
            lastWin32Error = syncOpenError;
            return CrossIntegrityProcessWaitOutcome.OpenFailed;
        }

        IntPtr limitedHandle = OpenProcess(
            ProcessQueryLimitedInformation,
            false,
            (uint)processId);
        if (limitedHandle == IntPtr.Zero)
        {
            int limitedOpenError = Marshal.GetLastWin32Error();
            if (limitedOpenError == ErrorInvalidParameter)
            {
                return CrossIntegrityProcessWaitOutcome.AlreadyExited;
            }

            lastWin32Error = limitedOpenError;
            return CrossIntegrityProcessWaitOutcome.OpenFailed;
        }

        try
        {
            if (!GetExitCodeProcess(limitedHandle, out uint initialExitCode))
            {
                lastWin32Error = Marshal.GetLastWin32Error();
                return CrossIntegrityProcessWaitOutcome.OpenFailed;
            }

            if (initialExitCode != StillActiveExitCode)
            {
                return CrossIntegrityProcessWaitOutcome.AlreadyExited;
            }

            if (clampedTimeoutMs == 0)
            {
                return CrossIntegrityProcessWaitOutcome.TimedOut;
            }

            long deadlineTick = Environment.TickCount64 + clampedTimeoutMs;
            while (true)
            {
                long remainingMs = deadlineTick - Environment.TickCount64;
                if (remainingMs <= 0)
                {
                    return GetExitCodeProcess(limitedHandle, out uint finalCode) &&
                           finalCode != StillActiveExitCode
                        ? CrossIntegrityProcessWaitOutcome.ExitedWithinTimeout
                        : CrossIntegrityProcessWaitOutcome.TimedOut;
                }

                Thread.Sleep((int)Math.Min(15, remainingMs));
                if (!GetExitCodeProcess(limitedHandle, out uint pollCode) ||
                    pollCode != StillActiveExitCode)
                {
                    return CrossIntegrityProcessWaitOutcome.ExitedWithinTimeout;
                }
            }
        }
        finally
        {
            _ = CloseHandle(limitedHandle);
        }
    }
}
