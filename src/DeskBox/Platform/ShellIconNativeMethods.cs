using System.Runtime.InteropServices;

namespace DeskBox.Platform;

/// <summary>
/// Shell icon extraction interop (SHGetFileInfo, SHDefExtractIcon,
/// ExtractIconEx, the IImageList COM interface, GDI HICON pixel extraction,
/// and the AssocChanged notification used to invalidate icon caches). Logic
/// and caching stay in the callers — this type only owns the native surface.
/// </summary>
internal static partial class ShellIconNativeMethods
{
    internal static unsafe uint ShFileInfoSize => (uint)sizeof(SHFILEINFO);

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        private fixed ushort _szDisplayName[260];
        private fixed ushort _szTypeName[80];

        public readonly string GetTypeName()
        {
            fixed (ushort* ptr = _szTypeName)
            {
                ReadOnlySpan<char> chars = new(ptr, 80);
                int nullIndex = chars.IndexOf('\0');
                ReadOnlySpan<char> slice = nullIndex >= 0 ? chars[..nullIndex] : chars;
                return slice.IsEmpty ? string.Empty : new string(slice);
            }
        }
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

    [LibraryImport("shell32.dll", EntryPoint = "SHGetFileInfoW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr SHGetFileInfo(
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
    [LibraryImport("shell32.dll", EntryPoint = "SHGetFileInfoW", SetLastError = true)]
    internal static partial IntPtr SHGetFileInfoPidl(
        IntPtr pszPath,
        uint dwFileAttributes,
        ref SHFILEINFO psfi,
        uint cbFileInfo,
        uint uFlags);

    [LibraryImport("shell32.dll", EntryPoint = "SHParseDisplayName", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int SHParseDisplayName(
        string pszName,
        IntPtr pbc,
        out IntPtr ppidl,
        uint sfgaoIn,
        out uint psfgaoOut);

    [LibraryImport("shell32.dll", EntryPoint = "ILFree")]
    internal static partial void ILFree(IntPtr pidl);

    [LibraryImport("user32.dll", EntryPoint = "DestroyIcon", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyIcon(IntPtr hIcon);

    [LibraryImport("user32.dll", EntryPoint = "GetIconInfo", SetLastError = true)]
    internal static partial int GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [LibraryImport("user32.dll", EntryPoint = "DrawIconEx", SetLastError = true)]
    internal static partial int DrawIconEx(
        IntPtr hdc,
        int xLeft,
        int yTop,
        IntPtr hIcon,
        int cxWidth,
        int cyWidth,
        uint istepIfAniCur,
        IntPtr hbrFlickerFreeDraw,
        uint diFlags);

    [LibraryImport("gdi32.dll", EntryPoint = "GetObjectW", SetLastError = true)]
    internal static partial int GetObjectBitmap(IntPtr h, int c, out BITMAP pv);

    [LibraryImport("gdi32.dll", EntryPoint = "GetDIBits", SetLastError = true)]
    internal static partial int GetDIBits(
        IntPtr hdc,
        IntPtr hbm,
        uint start,
        uint cLines,
        IntPtr lpvBits,
        ref BITMAPINFO lpbmi,
        uint usage);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateCompatibleDC", SetLastError = true)]
    internal static partial IntPtr CreateCompatibleDC(IntPtr hdc);

    [LibraryImport("gdi32.dll", EntryPoint = "DeleteDC", SetLastError = true)]
    internal static partial int DeleteDC(IntPtr hdc);

    [LibraryImport("gdi32.dll", EntryPoint = "SelectObject", SetLastError = true)]
    internal static partial IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [LibraryImport("gdi32.dll", EntryPoint = "DeleteObject", SetLastError = true)]
    internal static partial int DeleteObject(IntPtr ho);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateDIBSection", SetLastError = true)]
    internal static partial IntPtr CreateDIBSection(
        IntPtr hdc,
        ref BITMAPINFO pbmi,
        uint usage,
        out IntPtr ppvBits,
        IntPtr hSection,
        uint offset);

    [LibraryImport("shell32.dll", EntryPoint = "SHDefExtractIconW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static unsafe partial int SHDefExtractIcon(
        string pszIconFile,
        int iIcon,
        uint uFlags,
        out IntPtr phiconLarge,
        IntPtr* phiconSmall,
        uint nIconSize); // MAKELPARAM(cxLarge, cxSmall) — low word = large, high word = small

    [LibraryImport("shell32.dll", EntryPoint = "ExtractIconExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint ExtractIconEx(
        string lpszFile,
        int nIconIndex,
        Span<IntPtr> phiconLarge,
        Span<IntPtr> phiconSmall,
        uint nIcons);

    [LibraryImport("shell32.dll", EntryPoint = "SHGetImageList", SetLastError = true)]
    internal static partial int SHGetImageList(
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
    internal static unsafe int ImageListGetIcon(
        IntPtr imageList,
        int iconIndex,
        uint flags,
        ref IntPtr iconHandle)
    {
        var getIcon = (delegate* unmanaged[Stdcall]<IntPtr, int, uint, IntPtr*, int>)ComVtable.SlotPointer(imageList, 10);
        fixed (IntPtr* iconPtr = &iconHandle)
        {
            return getIcon(imageList, iconIndex, flags, iconPtr);
        }
    }

    internal const uint ILD_TRANSPARENT = 0x00000001;

    // Shell change notification for invalidating the icon cache.
    internal const int SHCNE_ASSOCCHANGED = 0x08000000;
    internal const uint SHCNF_IDLIST = 0x0000;

    [LibraryImport("shell32.dll", EntryPoint = "SHChangeNotify")]
    internal static partial void SHChangeNotify(
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
