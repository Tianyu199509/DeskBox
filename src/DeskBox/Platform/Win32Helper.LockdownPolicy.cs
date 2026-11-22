using System.Runtime.InteropServices;

namespace DeskBox.Platform;

public static partial class Win32Helper
{
    [LibraryImport("wldp.dll")]
    private static partial int WldpQueryWindowsLockdownMode(out int lockdownMode);

    /// <summary>
    /// Returns true when Windows 10/11 is running in S Mode lockdown (<c>WLDP_WINDOWS_LOCKDOWN_MODE_TRIAL</c>
    /// or <c>WLDP_WINDOWS_LOCKDOWN_MODE_LOCKED</c>).
    /// </summary>
    internal static bool IsWindowsInSModeLockdown()
    {
        try
        {
            return WldpQueryWindowsLockdownMode(out int lockdownMode) >= 0 && lockdownMode != 0;
        }
        catch
        {
            return false;
        }
    }
}
