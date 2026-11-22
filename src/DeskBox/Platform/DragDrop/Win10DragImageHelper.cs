using System.Globalization;
using System.Runtime.InteropServices;

namespace DeskBox.Platform.DragDrop;

internal readonly record struct Win10DragImageAttachResult(
    bool Attached,
    int HResult,
    int SizePx,
    int ItemCount,
    uint ColorKey);

/// <summary>
/// Attaches a DPI-scaled Shell drag bitmap and enables native drop description text
/// (<c>DSH_ALLOWDROPDESCRIPTIONTEXT</c>) via <c>CLSID_DragDropHelper</c> /
/// <c>IDragSourceHelper2</c> for Win10 native <c>SHDoDragDrop</c> sessions.
/// Verified against Windows SDK 10.0.26100.0 (<c>ShObjIdl_core.h</c> / <c>ShObjIdl.h</c>).
/// </summary>
internal static unsafe partial class Win10DragImageHelper
{
    private const uint ClsctxInprocServer = 0x1;
    private const uint DshAllowDropDescriptionText = 0x1;
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiLargeIcon = 0x000000000;
    private const uint ShgfiUseFileAttributes = 0x000000010;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint DiNormal = 0x0003;
    private const uint DibRgbColors = 0;
    private const uint BiRgb = 0;
    private const uint ColorKeyMagenta = 0x00FF00FF;

    private static readonly Guid ClsidDragDropHelper =
        new("4657278A-411B-11D2-839A-00C04FD918D0");

    private static readonly Guid IidIDragSourceHelper =
        new("DE5BF786-477A-11D2-839D-00C04FD918D0");

    private static readonly Guid IidIDragSourceHelper2 =
        new("83E07D0D-0C5F-4163-BF1A-60B274051E40");

    // Matches Windows SDK ShObjIdl_core.h (#include <pshpack8.h>):
    // SIZE (8B) + POINT (8B) + HBITMAP (8B on x64/ARM64) + COLORREF (4B + 4B tail pad) = 32B.
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeShDragImage
    {
        public int SizeCx;
        public int SizeCy;
        public int OffsetX;
        public int OffsetY;
        public nint BitmapHandle;
        public uint ColorKey;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint BiSize;
        public int BiWidth;
        public int BiHeight;
        public ushort BiPlanes;
        public ushort BiBitCount;
        public uint BiCompression;
        public uint BiSizeImage;
        public int BiXPelsPerMeter;
        public int BiYPelsPerMeter;
        public uint BiClrUsed;
        public uint BiClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShFileInfoW
    {
        public nint HIcon;
        public int IIcon;
        public uint DwAttributes;
        public fixed char SzDisplayName[260];
        public fixed char SzTypeName[80];
    }

    /// <summary>
    /// Best-effort attachment of a DPI-scaled Shell drag thumbnail + drop description
    /// support to <paramref name="dataObjectPtr"/>. Never throws on Shell helper errors.
    /// </summary>
    internal static bool TryAttachDragImage(
        nint dataObjectPtr,
        IReadOnlyList<string> sourcePaths,
        IntPtr hostWindowHandle) =>
        TryAttachDragImageWithDiagnostics(dataObjectPtr, sourcePaths, hostWindowHandle).Attached;

    internal static Win10DragImageAttachResult TryAttachDragImageWithDiagnostics(
        nint dataObjectPtr,
        IReadOnlyList<string> sourcePaths,
        IntPtr hostWindowHandle)
    {
        if (dataObjectPtr == 0 || sourcePaths.Count == 0)
        {
            return new Win10DragImageAttachResult(false, unchecked((int)0x80070057), 0, sourcePaths.Count, ColorKeyNone);
        }

        double dpiScale = ResolveHostDpiScale(hostWindowHandle);
        int sizePx = Math.Clamp((int)Math.Round(76.0 * dpiScale), 48, 192);
        nint hBitmap = TryCreateShellIconDragBitmap(sourcePaths[0], sizePx, sourcePaths.Count, out uint colorKey);
        if (hBitmap == 0)
        {
            return new Win10DragImageAttachResult(false, unchecked((int)0x80004005), sizePx, sourcePaths.Count, colorKey);
        }

        bool bitmapOwnershipTransferred = false;
        nint helper2Ptr = 0;
        nint helper1Ptr = 0;
        int lastHr = unchecked((int)0x80004005);
        try
        {
            Guid clsid = ClsidDragDropHelper;
            Guid iid2 = IidIDragSourceHelper2;
            int hr = CoCreateInstance(
                in clsid,
                0,
                ClsctxInprocServer,
                in iid2,
                out helper2Ptr);
            lastHr = hr;
            if (hr >= 0 && helper2Ptr != 0)
            {
                // IDragSourceHelper2 inherits IDragSourceHelper:
                // slot 3 = InitializeFromBitmap, slot 4 = InitializeFromWindow, slot 5 = SetFlags.
                nint* vtbl = *(nint**)helper2Ptr;
                var setFlags = (delegate* unmanaged[Stdcall]<nint, uint, int>)vtbl[5];
                _ = setFlags(helper2Ptr, DshAllowDropDescriptionText);

                var shdi = new NativeShDragImage
                {
                    SizeCx = sizePx,
                    SizeCy = sizePx,
                    OffsetX = sizePx / 2,
                    OffsetY = sizePx / 2,
                    BitmapHandle = hBitmap,
                    ColorKey = colorKey,
                };
                var initFromBitmap = (delegate* unmanaged[Stdcall]<nint, NativeShDragImage*, nint, int>)vtbl[3];
                int initHr = initFromBitmap(helper2Ptr, &shdi, dataObjectPtr);
                lastHr = initHr;
                if (initHr >= 0)
                {
                    bitmapOwnershipTransferred = true;
                    return new Win10DragImageAttachResult(true, initHr, sizePx, sourcePaths.Count, colorKey);
                }

                return new Win10DragImageAttachResult(false, initHr, sizePx, sourcePaths.Count, colorKey);
            }

            Guid iid1 = IidIDragSourceHelper;
            hr = CoCreateInstance(
                in clsid,
                0,
                ClsctxInprocServer,
                in iid1,
                out helper1Ptr);
            lastHr = hr;
            if (hr >= 0 && helper1Ptr != 0)
            {
                nint* vtbl = *(nint**)helper1Ptr;
                var shdi = new NativeShDragImage
                {
                    SizeCx = sizePx,
                    SizeCy = sizePx,
                    OffsetX = sizePx / 2,
                    OffsetY = sizePx / 2,
                    BitmapHandle = hBitmap,
                    ColorKey = colorKey,
                };
                var initFromBitmap = (delegate* unmanaged[Stdcall]<nint, NativeShDragImage*, nint, int>)vtbl[3];
                int initHr = initFromBitmap(helper1Ptr, &shdi, dataObjectPtr);
                lastHr = initHr;
                if (initHr >= 0)
                {
                    bitmapOwnershipTransferred = true;
                    return new Win10DragImageAttachResult(true, initHr, sizePx, sourcePaths.Count, colorKey);
                }
            }

            return new Win10DragImageAttachResult(false, lastHr, sizePx, sourcePaths.Count, colorKey);
        }
        catch (Exception ex)
        {
            return new Win10DragImageAttachResult(false, ex.HResult, sizePx, sourcePaths.Count, colorKey);
        }
        finally
        {
            if (helper2Ptr != 0)
            {
                ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)helper2Ptr)[2])(helper2Ptr);
            }

            if (helper1Ptr != 0)
            {
                ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)helper1Ptr)[2])(helper1Ptr);
            }

            if (!bitmapOwnershipTransferred && hBitmap != 0)
            {
                _ = DeleteObject(hBitmap);
            }
        }
    }

    private static double ResolveHostDpiScale(IntPtr hostWindowHandle)
    {
        try
        {
            if (hostWindowHandle != IntPtr.Zero)
            {
                uint dpi = GetDpiForWindow(hostWindowHandle);
                if (dpi >= 96 && dpi <= 480)
                {
                    return dpi / 96.0;
                }
            }
        }
        catch
        {
        }

        return 1.0;
    }

    internal const uint ColorKeyNone = 0xFFFFFFFFu;

    /// <summary>
    /// Selects the <c>SHDRAGIMAGE.crColorKey</c> after <c>DrawIconEx</c> renders
    /// onto a zero-filled 32-bit DIB: modern 32-bit alpha icons keep premultiplied
    /// transparent black (<c>0x00000000</c>) with <see cref="ColorKeyNone"/> so
    /// DWM/DirectComposition never blends magenta fringe; legacy 0-alpha icons
    /// backfill untouched background pixels with <c>ColorKeyMagenta</c>.
    /// </summary>
    internal static uint FinalizeDragBitmapColorKey(Span<uint> pixels)
    {
        for (int i = 0; i < pixels.Length; i++)
        {
            if ((pixels[i] & 0xFF000000u) != 0)
            {
                return ColorKeyNone;
            }
        }

        for (int i = 0; i < pixels.Length; i++)
        {
            if (pixels[i] == 0)
            {
                pixels[i] = ColorKeyMagenta;
            }
        }

        return ColorKeyMagenta;
    }

    private static nint TryCreateShellIconDragBitmap(
        string representativePath,
        int sizePx,
        int itemCount,
        out uint colorKey)
    {
        colorKey = ColorKeyNone;
        nint screenDc = GetDC(0);
        if (screenDc == 0)
        {
            return 0;
        }

        nint memDc = 0;
        nint hBitmap = 0;
        nint oldBitmap = 0;
        nint hIcon = 0;
        try
        {
            memDc = CreateCompatibleDC(screenDc);
            if (memDc == 0)
            {
                return 0;
            }

            var bmi = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    BiSize = (uint)sizeof(BitmapInfoHeader),
                    BiWidth = sizePx,
                    BiHeight = -sizePx, // Top-down 32-bit DIB
                    BiPlanes = 1,
                    BiBitCount = 32,
                    BiCompression = BiRgb,
                },
            };

            hBitmap = CreateDIBSection(memDc, &bmi, DibRgbColors, out nint bitsPtr, 0, 0);
            if (hBitmap == 0 || bitsPtr == 0)
            {
                return 0;
            }

            // Fill with valid premultiplied transparent black (A=0, R=0, G=0, B=0)
            // before drawing so 32-bit alpha icons never pick up magenta halo artifacts.
            int pixelCount = sizePx * sizePx;
            var pixelSpan = new Span<uint>((void*)bitsPtr, pixelCount);
            pixelSpan.Clear();

            oldBitmap = SelectObject(memDc, hBitmap);
            hIcon = TryExtractFileIcon(representativePath);
            if (hIcon != 0)
            {
                int iconSize = Math.Max(48, (sizePx * 3) / 4);
                iconSize = Math.Min(iconSize, sizePx - 8);
                int pad = (sizePx - iconSize) / 2;
                _ = DrawIconEx(memDc, pad, pad, hIcon, iconSize, iconSize, 0, 0, DiNormal);
            }

            SelectObject(memDc, oldBitmap);
            oldBitmap = 0;

            colorKey = FinalizeDragBitmapColorKey(pixelSpan);
            if (itemCount > 1)
            {
                DrawMultiFileCountBadge(
                    pixelSpan,
                    sizePx,
                    sizePx,
                    itemCount,
                    useAlphaChannel: colorKey == ColorKeyNone);
            }

            nint result = hBitmap;
            hBitmap = 0;
            return result;
        }
        catch
        {
            return 0;
        }
        finally
        {
            if (hIcon != 0)
            {
                _ = DestroyIcon(hIcon);
            }

            if (oldBitmap != 0 && memDc != 0)
            {
                _ = SelectObject(memDc, oldBitmap);
            }

            if (hBitmap != 0)
            {
                _ = DeleteObject(hBitmap);
            }

            if (memDc != 0)
            {
                _ = DeleteDC(memDc);
            }

            _ = ReleaseDC(0, screenDc);
        }
    }

    internal static void DrawMultiFileCountBadge(
        Span<uint> pixels,
        int width,
        int height,
        int itemCount,
        bool useAlphaChannel)
    {
        if (itemCount <= 1 || width < 32 || height < 32 || pixels.Length < width * height)
        {
            return;
        }

        string label = itemCount > 99 ? "99+" : itemCount.ToString(CultureInfo.InvariantCulture);
        int scale = Math.Max(1, width / 68);
        const int glyphW = 5;
        const int glyphH = 7;
        int charSpacing = scale;
        int textW = (label.Length * glyphW * scale) + Math.Max(0, label.Length - 1) * charSpacing;
        int textH = glyphH * scale;
        int padX = Math.Max(4, 4 * scale);
        int padY = Math.Max(3, 3 * scale);
        int badgeH = textH + (padY * 2);
        int badgeW = Math.Max(badgeH, textW + (padX * 2));
        int margin = Math.Max(2, 2 * scale);

        int right = width - margin;
        int bottom = height - margin;
        int left = Math.Max(0, right - badgeW);
        int top = Math.Max(0, bottom - badgeH);
        badgeW = right - left;
        badgeH = bottom - top;
        if (badgeW <= 4 || badgeH <= 4)
        {
            return;
        }

        uint alphaMask = useAlphaChannel ? 0xFF000000u : 0x00000000u;
        uint borderColor = alphaMask | 0x00FFFFFFu; // Crisp white halo
        uint fillColor = alphaMask | 0x000078D4u;   // Fluent accent blue (#0078D4 in BGRA)
        uint textColor = alphaMask | 0x00FFFFFFu;

        double radius = badgeH / 2.0;
        double innerRadius = Math.Max(1.0, radius - Math.Max(1.0, scale));
        double leftCenterX = left + radius;
        double rightCenterX = right - radius;
        double centerY = top + radius;

        for (int y = top; y < bottom; y++)
        {
            double dy = (y + 0.5) - centerY;
            int rowOffset = y * width;
            for (int x = left; x < right; x++)
            {
                double cx = x + 0.5;
                double dx = cx < leftCenterX
                    ? cx - leftCenterX
                    : (cx > rightCenterX ? cx - rightCenterX : 0.0);
                double distSq = (dx * dx) + (dy * dy);
                if (distSq <= innerRadius * innerRadius)
                {
                    pixels[rowOffset + x] = fillColor;
                }
                else if (distSq <= radius * radius)
                {
                    pixels[rowOffset + x] = borderColor;
                }
            }
        }

        int cursorX = left + ((badgeW - textW) / 2);
        int cursorY = top + ((badgeH - textH) / 2);
        for (int i = 0; i < label.Length; i++)
        {
            ReadOnlySpan<byte> rows = GetDigitGlyph5x7(label[i]);
            for (int gy = 0; gy < glyphH; gy++)
            {
                byte mask = rows[gy];
                for (int gx = 0; gx < glyphW; gx++)
                {
                    if ((mask & (1 << (glyphW - 1 - gx))) == 0)
                    {
                        continue;
                    }

                    int px0 = cursorX + (gx * scale);
                    int py0 = cursorY + (gy * scale);
                    for (int sy = 0; sy < scale; sy++)
                    {
                        int py = py0 + sy;
                        if ((uint)py >= (uint)height)
                        {
                            continue;
                        }

                        int rowOffset = py * width;
                        for (int sx = 0; sx < scale; sx++)
                        {
                            int px = px0 + sx;
                            if ((uint)px < (uint)width)
                            {
                                pixels[rowOffset + px] = textColor;
                            }
                        }
                    }
                }
            }

            cursorX += (glyphW * scale) + charSpacing;
        }
    }

    private static ReadOnlySpan<byte> GetDigitGlyph5x7(char ch) => ch switch
    {
        '0' => [0b01110, 0b10001, 0b10011, 0b10101, 0b11001, 0b10001, 0b01110],
        '1' => [0b00100, 0b01100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110],
        '2' => [0b01110, 0b10001, 0b00001, 0b00110, 0b01000, 0b10000, 0b11111],
        '3' => [0b11110, 0b00001, 0b00001, 0b01110, 0b00001, 0b00001, 0b11110],
        '4' => [0b00010, 0b00110, 0b01010, 0b10010, 0b11111, 0b00010, 0b00010],
        '5' => [0b11111, 0b10000, 0b11110, 0b00001, 0b00001, 0b10001, 0b01110],
        '6' => [0b01110, 0b10000, 0b11110, 0b10001, 0b10001, 0b10001, 0b01110],
        '7' => [0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b01000, 0b01000],
        '8' => [0b01110, 0b10001, 0b10001, 0b01110, 0b10001, 0b10001, 0b01110],
        '9' => [0b01110, 0b10001, 0b10001, 0b01111, 0b00001, 0b00001, 0b01110],
        '+' => [0b00000, 0b00100, 0b00100, 0b11111, 0b00100, 0b00100, 0b00000],
        _ => [0b00000, 0b00000, 0b00000, 0b00000, 0b00000, 0b00000, 0b00000],
    };

    private static nint TryExtractFileIcon(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return 0;
        }

        // Prefer 48x48 SHIL_EXTRALARGE from the system image list for a crisp,
        // full-frame icon without SHIL_JUMBO's top-left padding quirk.
        nint extraLarge = TryExtractExtraLargeShellIcon(path);
        if (extraLarge != 0)
        {
            return extraLarge;
        }

        ShFileInfoW info = default;
        uint flags = ShgfiIcon | ShgfiLargeIcon;
        nint res = SHGetFileInfoW(path, 0, &info, (uint)sizeof(ShFileInfoW), flags);
        if (res != 0 && info.HIcon != 0)
        {
            return info.HIcon;
        }

        uint attrs = Directory.Exists(path) ? FileAttributeDirectory : FileAttributeNormal;
        res = SHGetFileInfoW(path, attrs, &info, (uint)sizeof(ShFileInfoW), flags | ShgfiUseFileAttributes);
        return res != 0 ? info.HIcon : 0;
    }

    private static nint TryExtractExtraLargeShellIcon(string path)
    {
        nint imageListPtr = 0;
        try
        {
            var shinfo = new ShellIconNativeMethods.SHFILEINFO();
            IntPtr sysIndexRes = ShellIconNativeMethods.SHGetFileInfo(
                path,
                0,
                ref shinfo,
                ShellIconNativeMethods.ShFileInfoSize,
                ShellIconNativeMethods.SHGFI_SYSICONINDEX);
            if (sysIndexRes == IntPtr.Zero)
            {
                uint attrs = Directory.Exists(path) ? FileAttributeDirectory : FileAttributeNormal;
                sysIndexRes = ShellIconNativeMethods.SHGetFileInfo(
                    path,
                    attrs,
                    ref shinfo,
                    ShellIconNativeMethods.ShFileInfoSize,
                    ShellIconNativeMethods.SHGFI_SYSICONINDEX | ShellIconNativeMethods.SHGFI_USEFILEATTRIBUTES);
                if (sysIndexRes == IntPtr.Zero)
                {
                    return 0;
                }
            }

            Guid iid = ShellIconNativeMethods.IImageListIid;
            IntPtr listHandle = IntPtr.Zero;
            int hr = ShellIconNativeMethods.SHGetImageList(
                ShellIconNativeMethods.SHIL_EXTRALARGE,
                ref iid,
                ref listHandle);
            if (hr != 0 || listHandle == IntPtr.Zero)
            {
                return 0;
            }

            imageListPtr = listHandle;
            IntPtr hIcon = IntPtr.Zero;
            int iconHr = ShellIconNativeMethods.ImageListGetIcon(
                listHandle,
                shinfo.iIcon,
                ShellIconNativeMethods.ILD_TRANSPARENT,
                ref hIcon);
            return iconHr == 0 ? hIcon : 0;
        }
        catch
        {
            return 0;
        }
        finally
        {
            if (imageListPtr != 0)
            {
                ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)imageListPtr)[2])(imageListPtr);
            }
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(
        in Guid rclsid,
        nint pUnkOuter,
        uint dwClsContext,
        in Guid riid,
        out nint ppv);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint hWnd);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint hWnd, nint hDC);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateDIBSection(
        nint hdc,
        BitmapInfo* pbmi,
        uint usage,
        out nint ppvBits,
        nint hSection,
        uint offset);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint hdc, nint h);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint ho);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DrawIconEx(
        nint hdc,
        int xLeft,
        int yTop,
        nint hIcon,
        int cxWidth,
        int cyWidth,
        uint istepIfAniCur,
        nint hbrFlickerFreeDraw,
        uint diFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint hIcon);

    [LibraryImport("shell32.dll", EntryPoint = "SHGetFileInfoW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint SHGetFileInfoW(
        string pszPath,
        uint dwFileAttributes,
        ShFileInfoW* psfi,
        uint cbFileInfo,
        uint uFlags);
}
