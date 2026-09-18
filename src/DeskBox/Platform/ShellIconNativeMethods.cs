using System.Runtime.InteropServices;

namespace DeskBox.Platform;

/// <summary>
/// Shell icon extraction interop (SHGetFileInfo, SHDefExtractIcon,
/// ExtractIconEx, the IImageList COM interface, and the AssocChanged
/// notification used to invalidate icon caches). Logic and caching stay
/// in the callers — this type only owns the native surface.
/// </summary>
internal static class ShellIconNativeMethods
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    internal const uint SHGFI_ICON = 0x100;
    internal const uint SHGFI_LARGEICON = 0x0;
    internal const uint SHGFI_SYSICONINDEX = 0x4000;
    internal const uint SHGFI_USEFILEATTRIBUTES = 0x10;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        ref SHFILEINFO psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int SHDefExtractIcon(
        string pszIconFile,
        int iIcon,
        uint uFlags,
        out IntPtr phiconLarge,
        out IntPtr phiconSmall,
        uint nIconSize); // MAKELONG(cxSmall, cxLarge) — low word = small, high word = large

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint ExtractIconEx(
        string lpszFile,
        int nIconIndex,
        IntPtr[]? phiconLarge,
        IntPtr[]? phiconSmall,
        uint nIcons);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int SHGetImageList(
        int iImageList,
        ref Guid riid,
        ref IntPtr ppv);

    // Image list size flags for SHGetImageList
    internal const int SHIL_EXTRALARGE = 0x2; // 48x48
    internal const int SHIL_JUMBO = 0x4;      // 256x256 (Vista+)

    internal static readonly Guid IImageListIid =
        new("46EB5926-582E-4017-9FDF-E899822AA8B3");

    [ComImport]
    [Guid("46EB5926-582E-4017-9FDF-E899822AA8B3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IImageList
    {
        [PreserveSig]
        int GetImageCount();

        [PreserveSig]
        int GetImageRect(int i, ref Win32Helper.RECT pRect);

        [PreserveSig]
        int GetIcon(int i, uint flags, ref IntPtr picon);
    }

    internal const uint ILD_TRANSPARENT = 0x00000001;

    // Shell change notification for invalidating the icon cache.
    internal const int SHCNE_ASSOCCHANGED = 0x08000000;
    internal const uint SHCNF_IDLIST = 0x0000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern void SHChangeNotify(
        int wEventId,
        uint uFlags,
        IntPtr dwItem1,
        IntPtr dwItem2);
}
