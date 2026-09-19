using DeskBox.Platform;
using System.Text;

namespace DeskBox.Helpers;

/// <summary>
/// Identifies the Windows Shell desktop underneath the pointer. This is used
/// only when WinRT cannot expose a real StorageItem for an existing path (for
/// example, many .lnk files). In that case a rejected WinUI drag must not be
/// treated as a desktop drop merely because the pointer is inside a monitor's
/// work area.
/// </summary>
internal static partial class ShellDesktopDropTarget
{
    private const int MaxAncestorDepth = 16;



    internal static bool IsPointerOverDesktop()
    {
        if (!Win32Helper.GetCursorPos(out Win32Helper.POINT cursor))
        {
            return false;
        }

        return IsDesktopWindow(Win32Helper.WindowFromPoint(cursor));
    }

    internal static bool IsDesktopClassChain(
        IEnumerable<string> classNames)
    {
        bool hasDesktopClass = false;
        foreach (string className in classNames)
        {
            if (IsTaskbarClass(className))
            {
                return false;
            }

            hasDesktopClass |= IsDesktopClass(className);
        }

        return hasDesktopClass;
    }

    internal static bool IsDesktopWindow(IntPtr window)
    {
        if (window == IntPtr.Zero)
        {
            return false;
        }

        var classNames = new List<string>();
        IntPtr current = window;
        for (int depth = 0;
             current != IntPtr.Zero && depth < MaxAncestorDepth;
             depth++)
        {
            AddWindowClass(current, classNames);
            current = Win32Helper.GetParent(current);
        }

        IntPtr root = Win32Helper.GetAncestor(
            window,
            Win32Helper.GA_ROOT);
        AddWindowClass(root, classNames);
        if (!IsDesktopClassChain(classNames))
        {
            return false;
        }

        // WorkerW is used by more than one shell-adjacent surface. Restrict a
        // positive class match to Explorer whenever its process identity is
        // available, while retaining the class fallback during Explorer
        // restart when GetShellWindow temporarily returns zero.
        IntPtr shellWindow = Win32Helper.GetShellWindow();
        if (shellWindow == IntPtr.Zero)
        {
            return true;
        }

        Win32Helper.GetWindowThreadProcessId(
            shellWindow,
            out uint shellProcessId);
        Win32Helper.GetWindowThreadProcessId(
            root == IntPtr.Zero ? window : root,
            out uint targetProcessId);
        return shellProcessId == 0 ||
               targetProcessId == 0 ||
               shellProcessId == targetProcessId;
    }

    private static void AddWindowClass(
        IntPtr window,
        ICollection<string> classNames)
    {
        if (window == IntPtr.Zero)
        {
            return;
        }

        var buffer = new StringBuilder(256);
        if (Win32Helper.GetClassName(
                window,
                buffer,
                buffer.Capacity) > 0)
        {
            string className = buffer.ToString();
            if (!classNames.Contains(
                    className,
                    StringComparer.OrdinalIgnoreCase))
            {
                classNames.Add(className);
            }
        }
    }

    private static bool IsDesktopClass(string className)
    {
        return string.Equals(
                   className,
                   "Progman",
                   StringComparison.OrdinalIgnoreCase) ||
               string.Equals(
                   className,
                   "WorkerW",
                   StringComparison.OrdinalIgnoreCase) ||
               string.Equals(
                   className,
                   "SHELLDLL_DefView",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTaskbarClass(string className)
    {
        return string.Equals(
                   className,
                   "Shell_TrayWnd",
                   StringComparison.OrdinalIgnoreCase) ||
               string.Equals(
                   className,
                   "Shell_SecondaryTrayWnd",
                   StringComparison.OrdinalIgnoreCase) ||
               string.Equals(
                   className,
                   "NotifyIconOverflowWindow",
                   StringComparison.OrdinalIgnoreCase);
    }
}
