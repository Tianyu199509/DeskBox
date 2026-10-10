using System.Diagnostics;
using DeskBox.Platform;

namespace DeskBox.Services;

/// <summary>
/// Pure classification for the WindowsTap Start-menu mask race
/// (feedback 367). The Start menu surface belongs to
/// StartMenuExperienceHost.exe (Windows 10 1903+ and Windows 11) and its
/// CoreWindow exists even while the menu is closed, so "the menu opened"
/// can only be concluded from the FOREGROUND window's owning process.
/// </summary>
internal static class WindowsTapStartMenuLeakPolicy
{
    internal const string StartMenuHostProcessName = "StartMenuExperienceHost";

    internal static bool IsStartMenuHostProcessName(string? processName)
    {
        return !string.IsNullOrWhiteSpace(processName) &&
               string.Equals(
                   processName.Trim(),
                   StartMenuHostProcessName,
                   StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Samples the foreground window's owning process name for
/// <see cref="WindowsTapStartMenuLeakPolicy"/>. Win32-only; classification
/// itself stays pure for tests.
/// </summary>
internal static class WindowsTapStartMenuLeakProbe
{
    internal static bool TryGetForegroundProcessName(out string? processName)
    {
        processName = null;
        IntPtr foreground = Win32Helper.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        if (Win32Helper.GetWindowThreadProcessId(foreground, out uint processId) == 0 ||
            processId == 0)
        {
            return false;
        }

        try
        {
            using Process process = Process.GetProcessById((int)processId);
            processName = process.ProcessName;
            return true;
        }
        catch (ArgumentException)
        {
            // The foreground process exited between sampling the handle and
            // resolving its name.
            return false;
        }
    }
}
