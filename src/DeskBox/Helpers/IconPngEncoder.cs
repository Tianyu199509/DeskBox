using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using DeskBox.Platform;

namespace DeskBox.Helpers;

/// <summary>
/// Pure Win32 GDI + BCL <see cref="ZLibStream"/> HICON-to-PNG extractor/encoder.
/// Eliminates <c>System.Drawing.Common</c> and <c>gdiplus.dll</c> (plus GDI+'s
/// permanent background hook window and thread) from the resident process while
/// preserving full 32-bit straight-alpha icon fidelity.
/// </summary>
internal static class IconPngEncoder
{
    private const int MaxIconDimension = 1024;
    private static ReadOnlySpan<byte> PngSignature =>
        [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    private static ReadOnlySpan<byte> ChunkIhdr => [(byte)'I', (byte)'H', (byte)'D', (byte)'R'];
    private static ReadOnlySpan<byte> ChunkIdat => [(byte)'I', (byte)'D', (byte)'A', (byte)'T'];
    private static ReadOnlySpan<byte> ChunkIend => [(byte)'I', (byte)'E', (byte)'N', (byte)'D'];

    private static readonly uint[] s_crcTable = BuildCrc32Table();

    internal readonly record struct IconPixelBuffer(
        int Width,
        int Height,
        byte[] BgraPixels,
        byte[]? RentedBuffer = null) : IDisposable
    {
        public void Dispose()
        {
            if (RentedBuffer is not null)
            {
                ArrayPool<byte>.Shared.Return(RentedBuffer);
            }
        }
    }

    internal static unsafe bool TryExtractIconBitmap(
        IntPtr hIcon,
        out IconPixelBuffer bitmap)
    {
        bitmap = default;
        if (hIcon == IntPtr.Zero)
        {
            return false;
        }

        if (ShellIconNativeMethods.GetIconInfo(hIcon, out ShellIconNativeMethods.ICONINFO iconInfo) == 0)
        {
            return false;
        }

        IntPtr memDc = IntPtr.Zero;
        try
        {
            int width;
            int height;
            ushort colorBits = 0;

            if (iconInfo.hbmColor != IntPtr.Zero)
            {
                if (ShellIconNativeMethods.GetObjectBitmap(
                        iconInfo.hbmColor,
                        Marshal.SizeOf<ShellIconNativeMethods.BITMAP>(),
                        out ShellIconNativeMethods.BITMAP colorBmp) == 0)
                {
                    return false;
                }

                width = colorBmp.bmWidth;
                height = Math.Abs(colorBmp.bmHeight);
                colorBits = colorBmp.bmBitsPixel;
            }
            else if (iconInfo.hbmMask != IntPtr.Zero)
            {
                if (ShellIconNativeMethods.GetObjectBitmap(
                        iconInfo.hbmMask,
                        Marshal.SizeOf<ShellIconNativeMethods.BITMAP>(),
                        out ShellIconNativeMethods.BITMAP maskBmp) == 0)
                {
                    return false;
                }

                width = maskBmp.bmWidth;
                height = Math.Abs(maskBmp.bmHeight) / 2;
            }
            else
            {
                return false;
            }

            if (width <= 0 || height <= 0 || width > MaxIconDimension || height > MaxIconDimension)
            {
                return false;
            }

            memDc = ShellIconNativeMethods.CreateCompatibleDC(IntPtr.Zero);
            if (memDc == IntPtr.Zero)
            {
                return false;
            }

            int pixelByteLength = checked(width * height * 4);
            byte[] bgra = ArrayPool<byte>.Shared.Rent(pixelByteLength);
            bool succeeded = false;
            try
            {
                var bmi = new ShellIconNativeMethods.BITMAPINFO
                {
                    bmiHeader = new ShellIconNativeMethods.BITMAPINFOHEADER
                    {
                        biSize = (uint)Marshal.SizeOf<ShellIconNativeMethods.BITMAPINFOHEADER>(),
                        biWidth = width,
                        biHeight = -height, // top-down
                        biPlanes = 1,
                        biBitCount = 32,
                        biCompression = ShellIconNativeMethods.BI_RGB
                    }
                };

                if (iconInfo.hbmColor != IntPtr.Zero)
                {
                    int scanLines;
                    fixed (byte* bgraPtr = bgra)
                    {
                        scanLines = ShellIconNativeMethods.GetDIBits(
                            memDc,
                            iconInfo.hbmColor,
                            0,
                            (uint)height,
                            (IntPtr)bgraPtr,
                            ref bmi,
                            ShellIconNativeMethods.DIB_RGB_COLORS);
                    }

                    if (scanLines == 0)
                    {
                        return false;
                    }

                    if (colorBits == 32 &&
                        HasAnyNonZeroAlpha(bgra.AsSpan(0, pixelByteLength)))
                    {
                        bitmap = new IconPixelBuffer(width, height, bgra, bgra);
                        succeeded = true;
                        return true;
                    }

                    ApplyIconMaskAlpha(memDc, ref bmi, hIcon, width, height, bgra);
                    bitmap = new IconPixelBuffer(width, height, bgra, bgra);
                    succeeded = true;
                    return true;
                }

                // Monochrome / mask-only icon: draw RGB image then reconstruct alpha from DI_MASK.
                if (!TryRenderMonochromeIcon(memDc, ref bmi, hIcon, width, height, bgra))
                {
                    return false;
                }

                bitmap = new IconPixelBuffer(width, height, bgra, bgra);
                succeeded = true;
                return true;
            }
            finally
            {
                if (!succeeded)
                {
                    ArrayPool<byte>.Shared.Return(bgra);
                }
            }
        }
        finally
        {
            if (memDc != IntPtr.Zero)
            {
                _ = ShellIconNativeMethods.DeleteDC(memDc);
            }

            if (iconInfo.hbmColor != IntPtr.Zero)
            {
                _ = ShellIconNativeMethods.DeleteObject(iconInfo.hbmColor);
            }

            if (iconInfo.hbmMask != IntPtr.Zero)
            {
                _ = ShellIconNativeMethods.DeleteObject(iconInfo.hbmMask);
            }
        }
    }

    internal static byte[]? EncodeHIconAsPng(IntPtr hIcon)
    {
        if (!TryExtractIconBitmap(hIcon, out IconPixelBuffer bitmap))
        {
            return null;
        }

        using (bitmap)
        {
            if (!HasVisiblePixels(bitmap))
            {
                return null;
            }

            return EncodeRegionAsPng(bitmap, 0, 0, bitmap.Width, bitmap.Height);
        }
    }

    internal static bool HasVisiblePixels(IconPixelBuffer bitmap)
    {
        if (bitmap.Width <= 0 || bitmap.Height <= 0)
        {
            return false;
        }

        int byteLength = bitmap.Width * bitmap.Height * 4;
        return bitmap.BgraPixels.Length >= byteLength &&
               HasAnyNonZeroAlpha(bitmap.BgraPixels.AsSpan(0, byteLength));
    }

    internal static bool TryGetVisibleBounds(
        IconPixelBuffer bitmap,
        out int minX,
        out int minY,
        out int visibleWidth,
        out int visibleHeight)
    {
        minX = 0;
        minY = 0;
        visibleWidth = 0;
        visibleHeight = 0;

        if (bitmap.Width <= 0 || bitmap.Height <= 0 || bitmap.BgraPixels.Length < bitmap.Width * bitmap.Height * 4)
        {
            return false;
        }

        ReadOnlySpan<byte> pixels = bitmap.BgraPixels;
        int left = bitmap.Width;
        int top = bitmap.Height;
        int right = -1;
        int bottom = -1;
        int rowStride = bitmap.Width * 4;

        for (int y = 0; y < bitmap.Height; y++)
        {
            ReadOnlySpan<byte> rowSpan = pixels.Slice(y * rowStride, rowStride);
            if (!HasAnyNonZeroAlpha(rowSpan))
            {
                continue;
            }

            if (y < top)
            {
                top = y;
            }

            bottom = y;

            int leftScanLimit = Math.Min(bitmap.Width, left);
            for (int x = 0; x < leftScanLimit; x++)
            {
                if (rowSpan[(x * 4) + 3] != 0)
                {
                    left = x;
                    if (x > right)
                    {
                        right = x;
                    }

                    break;
                }
            }

            for (int x = bitmap.Width - 1; x > right; x--)
            {
                if (rowSpan[(x * 4) + 3] != 0)
                {
                    right = x;
                    if (x < left)
                    {
                        left = x;
                    }

                    break;
                }
            }
        }

        if (right < left || bottom < top)
        {
            return false;
        }

        minX = left;
        minY = top;
        visibleWidth = right - left + 1;
        visibleHeight = bottom - top + 1;
        return true;
    }

    internal static byte[] EncodeRegionAsPng(
        IconPixelBuffer bitmap,
        int cropX,
        int cropY,
        int cropWidth,
        int cropHeight)
    {
        using var compressed = new MemoryStream();
        int scanlineByteLength = checked((cropWidth * 4) + 1);
        byte[] rentedScanline = ArrayPool<byte>.Shared.Rent(scanlineByteLength);
        try
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                ReadOnlySpan<byte> src = bitmap.BgraPixels;
                int srcStride = bitmap.Width * 4;
                rentedScanline[0] = 0; // Filter type 0 (None)

                int rowByteLength = cropWidth * 4;
                for (int y = 0; y < cropHeight; y++)
                {
                    int srcRowOffset = ((cropY + y) * srcStride) + (cropX * 4);
                    SwizzleBgraToRgba(
                        src.Slice(srcRowOffset, rowByteLength),
                        rentedScanline.AsSpan(1, rowByteLength));
                    zlib.Write(rentedScanline, 0, scanlineByteLength);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedScanline);
        }

        byte[] idatPayload = compressed.ToArray();
        int totalPngBytes = 8 + (12 + 13) + (12 + idatPayload.Length) + 12;
        using var output = new MemoryStream(totalPngBytes);
        output.Write(PngSignature);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr[..4], (uint)cropWidth);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.Slice(4, 4), (uint)cropHeight);
        ihdr[8] = 8; // bit depth: 8
        ihdr[9] = 6; // color type: 6 (RGBA)
        ihdr[10] = 0; // compression: deflate
        ihdr[11] = 0; // filter: adaptive
        ihdr[12] = 0; // interlace: none
        WriteChunk(output, ChunkIhdr, ihdr);
        WriteChunk(output, ChunkIdat, idatPayload);
        WriteChunk(output, ChunkIend, ReadOnlySpan<byte>.Empty);
        return output.ToArray();
    }

    /// <summary>
    /// Decodes an 8-bit RGBA PNG produced by <see cref="EncodeRegionAsPng"/> (or
    /// standard PNG encoders) and checks whether at least one pixel has non-zero alpha.
    /// </summary>
    internal static bool TryDecodePngHasVisiblePixels(ReadOnlySpan<byte> pngBytes)
    {
        if (pngBytes.Length < 45 || !pngBytes[..8].SequenceEqual(PngSignature))
        {
            return false;
        }

        int width = 0;
        int height = 0;
        byte bitDepth = 0;
        byte colorType = 0;
        using var idatConcat = new MemoryStream();

        int offset = 8;
        while (offset + 12 <= pngBytes.Length)
        {
            uint chunkLength = BinaryPrimitives.ReadUInt32BigEndian(pngBytes.Slice(offset, 4));
            if (chunkLength > (uint)(pngBytes.Length - offset - 12))
            {
                return false;
            }

            ReadOnlySpan<byte> chunkType = pngBytes.Slice(offset + 4, 4);
            ReadOnlySpan<byte> chunkData = pngBytes.Slice(offset + 8, (int)chunkLength);
            if (chunkType.SequenceEqual(ChunkIhdr))
            {
                if (chunkData.Length < 13)
                {
                    return false;
                }

                width = (int)BinaryPrimitives.ReadUInt32BigEndian(chunkData[..4]);
                height = (int)BinaryPrimitives.ReadUInt32BigEndian(chunkData.Slice(4, 4));
                bitDepth = chunkData[8];
                colorType = chunkData[9];
            }
            else if (chunkType.SequenceEqual(ChunkIdat))
            {
                idatConcat.Write(chunkData);
            }
            else if (chunkType.SequenceEqual(ChunkIend))
            {
                break;
            }

            offset += 12 + (int)chunkLength;
        }

        if (width <= 0 || height <= 0 || bitDepth != 8 || (colorType != 6 && colorType != 2))
        {
            return false;
        }

        if (colorType == 2)
        {
            // Opaque RGB image always has visible pixels when dimensions > 0.
            return idatConcat.Length > 0;
        }

        idatConcat.Position = 0;
        int rowPixelsBytes = checked(width * 4);
        int scanlineBytes = rowPixelsBytes + 1;
        byte[] prevRow = new byte[rowPixelsBytes];
        byte[] currRow = new byte[rowPixelsBytes];
        byte[] scanline = new byte[scanlineBytes];

        using var zlib = new ZLibStream(idatConcat, CompressionMode.Decompress);
        for (int y = 0; y < height; y++)
        {
            zlib.ReadExactly(scanline, 0, scanlineBytes);
            byte filter = scanline[0];
            UnfilterRgbaScanline(filter, scanline.AsSpan(1, rowPixelsBytes), prevRow, currRow);

            if (HasAnyNonZeroAlpha(currRow))
            {
                return true;
            }

            (prevRow, currRow) = (currRow, prevRow);
        }

        return false;
    }

    private static void UnfilterRgbaScanline(
        byte filter,
        ReadOnlySpan<byte> raw,
        ReadOnlySpan<byte> prev,
        Span<byte> dst)
    {
        const int bpp = 4;
        for (int i = 0; i < raw.Length; i++)
        {
            byte left = i >= bpp ? dst[i - bpp] : (byte)0;
            byte up = prev[i];
            byte upLeft = i >= bpp ? prev[i - bpp] : (byte)0;
            dst[i] = filter switch
            {
                0 => raw[i],
                1 => (byte)(raw[i] + left),
                2 => (byte)(raw[i] + up),
                3 => (byte)(raw[i] + ((left + up) >> 1)),
                4 => (byte)(raw[i] + PaethPredictor(left, up, upLeft)),
                _ => raw[i]
            };
        }
    }

    private static byte PaethPredictor(byte a, byte b, byte c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }

    internal static void SwizzleBgraToRgba(
        ReadOnlySpan<byte> srcBgra,
        Span<byte> dstRgba,
        bool forceOpaqueAlpha = false)
    {
        int byteLength = Math.Min(srcBgra.Length, dstRgba.Length) & ~3;
        if (byteLength <= 0)
        {
            return;
        }

        ReadOnlySpan<byte> src = srcBgra[..byteLength];
        Span<byte> dst = dstRgba[..byteLength];

        if (Vector128.IsHardwareAccelerated)
        {
            if (byteLength >= Vector128<byte>.Count)
            {
                Vector128<byte> shuffleIndices = Vector128.Create(
                    (byte)2, 1, 0, 3,
                    6, 5, 4, 7,
                    10, 9, 8, 11,
                    14, 13, 12, 15);
                Vector128<byte> opaqueAlphaMask = forceOpaqueAlpha
                    ? Vector128.Create(
                        (byte)0, 0, 0, 255,
                        0, 0, 0, 255,
                        0, 0, 0, 255,
                        0, 0, 0, 255)
                    : Vector128<byte>.Zero;

                int tailOffset = byteLength - Vector128<byte>.Count;
                Vector128<byte> srcEnd = Vector128.Create<byte>(src[tailOffset..]);
                ReadOnlySpan<byte> srcRem = src;
                Span<byte> dstRem = dst;

                while (srcRem.Length >= Vector128<byte>.Count)
                {
                    (Vector128.Shuffle(Vector128.Create<byte>(srcRem), shuffleIndices) | opaqueAlphaMask)
                        .CopyTo(dstRem);
                    srcRem = srcRem[Vector128<byte>.Count..];
                    dstRem = dstRem[Vector128<byte>.Count..];
                }

                if (!srcRem.IsEmpty)
                {
                    (Vector128.Shuffle(srcEnd, shuffleIndices) | opaqueAlphaMask)
                        .CopyTo(dst[tailOffset..]);
                }

                return;
            }

            SwizzleBgraToRgbaScalar(src, dst, forceOpaqueAlpha);
            return;
        }

        SwizzleBgraToRgbaScalar(src, dst, forceOpaqueAlpha);
    }

    private static void SwizzleBgraToRgbaScalar(
        ReadOnlySpan<byte> src,
        Span<byte> dst,
        bool forceOpaqueAlpha)
    {
        for (int i = 0; i < src.Length; i += 4)
        {
            byte b = src[i];
            byte g = src[i + 1];
            byte r = src[i + 2];
            byte a = forceOpaqueAlpha ? (byte)255 : src[i + 3];
            dst[i] = r;
            dst[i + 1] = g;
            dst[i + 2] = b;
            dst[i + 3] = a;
        }
    }

    internal static void InspectBgraAlphaAndColor(
        ReadOnlySpan<byte> bgra,
        out bool hasAnyAlpha,
        out bool hasAnyColor)
    {
        hasAnyAlpha = false;
        hasAnyColor = false;
        int byteLength = bgra.Length & ~3;
        if (byteLength <= 0)
        {
            return;
        }

        ReadOnlySpan<byte> aligned = bgra[..byteLength];
        if (Vector128.IsHardwareAccelerated)
        {
            if (byteLength >= Vector128<byte>.Count)
            {
                Vector128<byte> alphaMask = Vector128.Create(
                    (byte)0, 0, 0, 255,
                    0, 0, 0, 255,
                    0, 0, 0, 255,
                    0, 0, 0, 255);
                Vector128<byte> colorOr = Vector128<byte>.Zero;
                ReadOnlySpan<byte> rem = aligned;

                while (rem.Length >= Vector128<byte>.Count)
                {
                    Vector128<byte> v = Vector128.Create<byte>(rem);
                    if ((v & alphaMask) != Vector128<byte>.Zero)
                    {
                        hasAnyAlpha = true;
                        return;
                    }

                    colorOr |= v;
                    rem = rem[Vector128<byte>.Count..];
                }

                if (!rem.IsEmpty)
                {
                    Vector128<byte> tail = Vector128.Create<byte>(aligned[(byteLength - Vector128<byte>.Count)..]);
                    if ((tail & alphaMask) != Vector128<byte>.Zero)
                    {
                        hasAnyAlpha = true;
                        return;
                    }

                    colorOr |= tail;
                }

                hasAnyColor = colorOr != Vector128<byte>.Zero;
                return;
            }

            InspectBgraAlphaAndColorScalar(aligned, out hasAnyAlpha, out hasAnyColor);
            return;
        }

        InspectBgraAlphaAndColorScalar(aligned, out hasAnyAlpha, out hasAnyColor);
    }

    private static void InspectBgraAlphaAndColorScalar(
        ReadOnlySpan<byte> aligned,
        out bool hasAnyAlpha,
        out bool hasAnyColor)
    {
        hasAnyAlpha = false;
        hasAnyColor = false;
        for (int i = 0; i < aligned.Length; i += 4)
        {
            if (aligned[i + 3] != 0)
            {
                hasAnyAlpha = true;
                return;
            }

            if ((aligned[i] | aligned[i + 1] | aligned[i + 2]) != 0)
            {
                hasAnyColor = true;
            }
        }
    }

    internal static bool HasAnyNonZeroAlpha(ReadOnlySpan<byte> bgra)
    {
        int byteLength = bgra.Length & ~3;
        if (byteLength <= 0)
        {
            return false;
        }

        ReadOnlySpan<byte> aligned = bgra[..byteLength];
        if (Vector128.IsHardwareAccelerated)
        {
            if (byteLength >= Vector128<byte>.Count)
            {
                Vector128<byte> alphaMask = Vector128.Create(
                    (byte)0, 0, 0, 255,
                    0, 0, 0, 255,
                    0, 0, 0, 255,
                    0, 0, 0, 255);
                ReadOnlySpan<byte> rem = aligned;

                while (rem.Length >= Vector128<byte>.Count)
                {
                    if ((Vector128.Create<byte>(rem) & alphaMask) != Vector128<byte>.Zero)
                    {
                        return true;
                    }

                    rem = rem[Vector128<byte>.Count..];
                }

                return !rem.IsEmpty &&
                       (Vector128.Create<byte>(aligned[(byteLength - Vector128<byte>.Count)..]) & alphaMask) != Vector128<byte>.Zero;
            }

            return HasAnyNonZeroAlphaScalar(aligned);
        }

        return HasAnyNonZeroAlphaScalar(aligned);
    }

    private static bool HasAnyNonZeroAlphaScalar(ReadOnlySpan<byte> aligned)
    {
        for (int i = 3; i < aligned.Length; i += 4)
        {
            if (aligned[i] != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static unsafe void ApplyIconMaskAlpha(
        IntPtr memDc,
        ref ShellIconNativeMethods.BITMAPINFO bmi,
        IntPtr hIcon,
        int width,
        int height,
        byte[] bgra)
    {
        int byteLength = width * height * 4;
        IntPtr maskDib = ShellIconNativeMethods.CreateDIBSection(
            memDc,
            ref bmi,
            ShellIconNativeMethods.DIB_RGB_COLORS,
            out IntPtr maskBits,
            IntPtr.Zero,
            0);
        if (maskDib == IntPtr.Zero || maskBits == IntPtr.Zero)
        {
            for (int i = 3; i < byteLength; i += 4)
            {
                bgra[i] = 255;
            }

            return;
        }

        IntPtr oldBmp = ShellIconNativeMethods.SelectObject(memDc, maskDib);
        try
        {
            int pixelCount = width * height;
            new Span<uint>((void*)maskBits, pixelCount).Fill(0xFFFFFFFFu);
            if (ShellIconNativeMethods.DrawIconEx(
                    memDc,
                    0,
                    0,
                    hIcon,
                    width,
                    height,
                    0,
                    IntPtr.Zero,
                    ShellIconNativeMethods.DI_MASK) != 0)
            {
                byte* maskBytes = (byte*)maskBits;
                for (int i = 0; i < pixelCount; i++)
                {
                    int offset = i * 4;
                    bgra[offset + 3] = maskBytes[offset] == 0 ? (byte)255 : (byte)0;
                }
            }
            else
            {
                for (int i = 3; i < byteLength; i += 4)
                {
                    bgra[i] = 255;
                }
            }
        }
        finally
        {
            _ = ShellIconNativeMethods.SelectObject(memDc, oldBmp);
            _ = ShellIconNativeMethods.DeleteObject(maskDib);
        }
    }

    private static unsafe bool TryRenderMonochromeIcon(
        IntPtr memDc,
        ref ShellIconNativeMethods.BITMAPINFO bmi,
        IntPtr hIcon,
        int width,
        int height,
        byte[] bgra)
    {
        IntPtr colorDib = ShellIconNativeMethods.CreateDIBSection(
            memDc,
            ref bmi,
            ShellIconNativeMethods.DIB_RGB_COLORS,
            out IntPtr colorBits,
            IntPtr.Zero,
            0);
        if (colorDib == IntPtr.Zero || colorBits == IntPtr.Zero)
        {
            return false;
        }

        IntPtr oldBmp = ShellIconNativeMethods.SelectObject(memDc, colorDib);
        try
        {
            int pixelCount = width * height;
            int byteLength = checked(pixelCount * 4);
            new Span<uint>((void*)colorBits, pixelCount).Clear();
            if (ShellIconNativeMethods.DrawIconEx(
                    memDc,
                    0,
                    0,
                    hIcon,
                    width,
                    height,
                    0,
                    IntPtr.Zero,
                    ShellIconNativeMethods.DI_NORMAL) == 0)
            {
                return false;
            }

            new ReadOnlySpan<byte>((void*)colorBits, byteLength).CopyTo(bgra.AsSpan(0, byteLength));
        }
        finally
        {
            _ = ShellIconNativeMethods.SelectObject(memDc, oldBmp);
            _ = ShellIconNativeMethods.DeleteObject(colorDib);
        }

        ApplyIconMaskAlpha(memDc, ref bmi, hIcon, width, height, bgra);
        return true;
    }

    private static void WriteChunk(
        Stream output,
        ReadOnlySpan<byte> chunkType,
        ReadOnlySpan<byte> data)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header[..4], (uint)data.Length);
        chunkType.CopyTo(header.Slice(4, 4));
        output.Write(header);
        if (!data.IsEmpty)
        {
            output.Write(data);
        }

        uint crc = ComputeCrc32(chunkType, data);
        Span<byte> trailer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(trailer, crc);
        output.Write(trailer);
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in first)
        {
            crc = s_crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        foreach (byte b in second)
        {
            crc = s_crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
