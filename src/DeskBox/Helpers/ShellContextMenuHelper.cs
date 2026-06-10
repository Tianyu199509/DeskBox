using System.Runtime.InteropServices;
using DeskBox.Helpers;

namespace DeskBox.Helpers;

/// <summary>
/// Helper to show the native Windows Explorer context menu for a file or folder.
/// Note: This is a simplified wrapper. For full native menus including Windows 11 modern menus,
/// complex COM interop with IContextMenu/IContextMenu2/IContextMenu3 is required.
/// </summary>
public static class ShellContextMenuHelper
{
    // Simplified: For this lightweight version, we will just open the file properties
    // or standard verbs if we can't fully host the IContextMenu due to WinUI 3 Threading complexities.

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SHObjectProperties(IntPtr hwnd, uint shopObjectType, string pszObjectName, string? pszPropertyPage);

    private const uint SHOP_FILEPATH = 0x2;

    /// <summary>
    /// Show the native properties dialog for a file.
    /// </summary>
    public static void ShowProperties(IntPtr hwnd, string filePath)
    {
        SHObjectProperties(hwnd, SHOP_FILEPATH, filePath, null);
    }
}
