using System.Runtime.InteropServices;

namespace DeskBox.Platform;

/// <summary>
/// Shell icon extraction interop (SHGetFileInfo, SHDefExtractIcon,
/// ExtractIconEx, the IImageList COM interface, GDI HICON pixel extraction,
/// and the AssocChanged notification used to invalidate icon caches). Logic
/// and caching stay in the callers — this type only owns the native surface.
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

    [StructLayout(LayoutKind.Sequential)]
    internal struct ICONINFO
    {
        public int fIcon;
        public uint xHotspot;
        public uint yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors0;
    }

    internal const uint SHGFI_ICON = 0x100;
    internal const uint SHGFI_LARGEICON = 0x0;
    internal const uint SHGFI_SYSICONINDEX = 0x4000;
    internal const uint SHGFI_USEFILEATTRIBUTES = 0x10;
    internal const uint SHGFI_PIDL = 0x8;
    internal const uint SHGFI_TYPENAME = 0x400;

    internal const uint BI_RGB = 0;
    internal const uint DIB_RGB_COLORS = 0;
    internal const uint DI_MASK = 0x0001;
    internal const uint DI_IMAGE = 0x0002;
    internal const uint DI_NORMAL = 0x0003;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        ref SHFILEINFO psfi,
        uint cbFileInfo,
        uint uFlags);

    /// <summary>
    /// PIDL flavor for shell-namespace items that have no file-system path
    /// (Recycle Bin, Control Panel). The Recycle Bin's icon is state-aware —
    /// the shell returns the empty or the full graphic for the same PIDL.
    /// </summary>
    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", SetLastError = true)]
    internal static extern IntPtr SHGetFileInfoPidl(
        IntPtr pszPath,
        uint dwFileAttributes,
        ref SHFILEINFO psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int SHParseDisplayName(
        string pszName,
        IntPtr pbc,
        out IntPtr ppidl,
        uint sfgaoIn,
        out uint psfgaoOut);

    [DllImport("shell32.dll")]
    internal static extern void ILFree(IntPtr pidl);

    [DllImport("user32.dll", EntryPoint = "DestroyIcon", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", EntryPoint = "GetIconInfo", ExactSpelling = true, SetLastError = true)]
    internal static extern int GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll", EntryPoint = "DrawIconEx", ExactSpelling = true, SetLastError = true)]
    internal static extern int DrawIconEx(
        IntPtr hdc,
        int xLeft,
        int yTop,
        IntPtr hIcon,
        int cxWidth,
        int cyWidth,
        uint istepIfAniCur,
        IntPtr hbrFlickerFreeDraw,
        uint diFlags);

    [DllImport("gdi32.dll", EntryPoint = "GetObjectW", ExactSpelling = true, SetLastError = true)]
    internal static extern int GetObjectBitmap(IntPtr h, int c, out BITMAP pv);

    [DllImport("gdi32.dll", EntryPoint = "GetDIBits", ExactSpelling = true, SetLastError = true)]
    internal static extern int GetDIBits(
        IntPtr hdc,
        IntPtr hbm,
        uint start,
        uint cLines,
        IntPtr lpvBits,
        ref BITMAPINFO lpbmi,
        uint usage);

    [DllImport("gdi32.dll", EntryPoint = "CreateCompatibleDC", ExactSpelling = true, SetLastError = true)]
    internal static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", EntryPoint = "DeleteDC", ExactSpelling = true, SetLastError = true)]
    internal static extern int DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", EntryPoint = "SelectObject", ExactSpelling = true, SetLastError = true)]
    internal static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll", EntryPoint = "DeleteObject", ExactSpelling = true, SetLastError = true)]
    internal static extern int DeleteObject(IntPtr ho);

    [DllImport("gdi32.dll", EntryPoint = "CreateDIBSection", ExactSpelling = true, SetLastError = true)]
    internal static extern IntPtr CreateDIBSection(
        IntPtr hdc,
        ref BITMAPINFO pbmi,
        uint usage,
        out IntPtr ppvBits,
        IntPtr hSection,
        uint offset);

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
    internal const int SHIL_LARGE = 0x0;      // 32x32
    internal const int SHIL_EXTRALARGE = 0x2; // 48x48
    internal const int SHIL_JUMBO = 0x4;      // 256x256 (Vista+)

    internal static readonly Guid IImageListIid =
        new("46EB5926-582E-4017-9FDF-E899822AA8B3");

    // IImageList::GetIcon via a raw vtable slot (see ComVtable): classic
    // ComImport RCW marshaling does not exist on NativeAOT, and the old
    // ComImport declaration listed GetImageCount/GetImageRect/GetIcon as the
    // first three slots — the real order per commoncontrols.h is
    // Add(3), ReplaceIcon(4), SetOverlayImage(5), Replace(6), AddMasked(7),
    // Draw(8), Remove(9), GetIcon(10) — so the RCW call landed on
    // SetOverlayImage's slot and this path silently fell to its null
    // fallback even on JIT.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int ImageListGetIconInvoker(
        IntPtr self, int i, uint flags, ref IntPtr picon);

    internal static int ImageListGetIcon(
        IntPtr imageList,
        int iconIndex,
        uint flags,
        ref IntPtr iconHandle)
    {
        return ComVtable.Slot<ImageListGetIconInvoker>(imageList, 10)(
            imageList,
            iconIndex,
            flags,
            ref iconHandle);
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

    internal static bool ProbeIconBitmapEntryPoints()
    {
        IntPtr user32 = NativeLibrary.Load("user32.dll");
        IntPtr gdi32 = NativeLibrary.Load("gdi32.dll");
        try
        {
            return NativeLibrary.TryGetExport(user32, "GetIconInfo", out _) &&
                   NativeLibrary.TryGetExport(user32, "DrawIconEx", out _) &&
                   NativeLibrary.TryGetExport(user32, "DestroyIcon", out _) &&
                   NativeLibrary.TryGetExport(gdi32, "GetObjectW", out _) &&
                   NativeLibrary.TryGetExport(gdi32, "GetDIBits", out _) &&
                   NativeLibrary.TryGetExport(gdi32, "CreateCompatibleDC", out _) &&
                   NativeLibrary.TryGetExport(gdi32, "DeleteDC", out _) &&
                   NativeLibrary.TryGetExport(gdi32, "SelectObject", out _) &&
                   NativeLibrary.TryGetExport(gdi32, "DeleteObject", out _) &&
                   NativeLibrary.TryGetExport(gdi32, "CreateDIBSection", out _);
        }
        finally
        {
            NativeLibrary.Free(gdi32);
            NativeLibrary.Free(user32);
        }
    }
}
