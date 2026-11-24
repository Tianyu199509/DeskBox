using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;

namespace DeskBox.Helpers;

public static partial class IconHelper
{
    private static readonly uint[] s_pngCrc32Table = CreatePngCrc32Table();

    private static uint[] CreatePngCrc32Table()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint crc = i;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0
                    ? 0xEDB88320u ^ (crc >> 1)
                    : crc >> 1;
            }

            table[i] = crc;
        }

        return table;
    }

    /// <summary>
    /// Transcodes raw 32-bit BGRA BMP / BMPv5 payloads (such as the 262 KB
    /// 256x256 payloads returned by <see cref="ShellThumbnailProxy"/>) into
    /// compact RGBA PNGs without allocating Large Object Heap buffers.
    /// Already-encoded PNG/JPEG/ICO payloads pass through untouched.
    /// </summary>
    internal static byte[] CompressBmpPayloadToPngIfPossible(byte[] bytes)
    {
        if (bytes.Length < 54 ||
            bytes[0] != (byte)'B' ||
            bytes[1] != (byte)'M')
        {
            return bytes;
        }

        ReadOnlySpan<byte> span = bytes;
        uint pixelOffset = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(10, 4));
        uint dibHeaderSize = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(14, 4));
        if (dibHeaderSize < 40 || 14u + dibHeaderSize > pixelOffset)
        {
            return bytes;
        }

        int width = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(18, 4));
        int rawHeight = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(22, 4));
        ushort planes = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(26, 2));
        ushort bitCount = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(28, 2));
        uint compression = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(30, 4));
        if (planes != 1 ||
            bitCount != 32 ||
            width is <= 0 or > 1024 ||
            rawHeight == int.MinValue)
        {
            return bytes;
        }

        if (compression == 3)
        {
            if (dibHeaderSize < 52 || span.Length < 14 + 52)
            {
                return bytes;
            }

            uint redMask = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(54, 4));
            uint greenMask = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(58, 4));
            uint blueMask = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(62, 4));
            uint alphaMask = dibHeaderSize >= 56 && span.Length >= 14 + 56
                ? BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(66, 4))
                : 0u;
            if (redMask != 0x00FF0000u ||
                greenMask != 0x0000FF00u ||
                blueMask != 0x000000FFu ||
                (alphaMask != 0u && alphaMask != 0xFF000000u))
            {
                return bytes;
            }
        }
        else if (compression != 0)
        {
            return bytes;
        }

        int height = Math.Abs(rawHeight);
        if (height is <= 0 or > 1024)
        {
            return bytes;
        }

        int rowBytes = width * 4;
        long totalPixelBytes = (long)rowBytes * height;
        if (pixelOffset > (uint)bytes.Length ||
            bytes.Length - (int)pixelOffset < totalPixelBytes)
        {
            return bytes;
        }

        bool topDown = rawHeight < 0;
        ReadOnlySpan<byte> pixels = span.Slice((int)pixelOffset, (int)totalPixelBytes);
        IconPngEncoder.InspectBgraAlphaAndColor(pixels, out bool hasAnyAlpha, out bool hasAnyColor);

        bool forceOpaqueAlpha = !hasAnyAlpha && hasAnyColor;
        int scanlineLength = 1 + rowBytes;
        byte[] rentedScanline = ArrayPool<byte>.Shared.Rent(scanlineLength);
        try
        {
            using var compressedStream = new MemoryStream(
                Math.Min(16384, Math.Max(512, bytes.Length / 8)));
            using (var zlib = new ZLibStream(
                compressedStream,
                CompressionLevel.Fastest,
                leaveOpen: true))
            {
                rentedScanline[0] = 0; // Filter 0 (None)
                for (int y = 0; y < height; y++)
                {
                    int srcRow = topDown ? y : (height - 1 - y);
                    ReadOnlySpan<byte> src = pixels.Slice(srcRow * rowBytes, rowBytes);
                    Span<byte> dst = rentedScanline.AsSpan(1, rowBytes);
                    IconPngEncoder.SwizzleBgraToRgba(src, dst, forceOpaqueAlpha);
                    zlib.Write(rentedScanline, 0, scanlineLength);
                }
            }

            if (!compressedStream.TryGetBuffer(out ArraySegment<byte> idatPayload))
            {
                return bytes;
            }

            // PNG = 8-byte signature + IHDR (25B) + IDAT (12B + payload) + IEND (12B)
            int pngLength = 8 + 25 + 12 + idatPayload.Count + 12;
            if (pngLength >= bytes.Length)
            {
                return bytes;
            }

            byte[] png = new byte[pngLength];
            Span<byte> outSpan = png;
            ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
            signature.CopyTo(outSpan);
            int offset = 8;

            Span<byte> ihdr = stackalloc byte[13];
            BinaryPrimitives.WriteInt32BigEndian(ihdr.Slice(0, 4), width);
            BinaryPrimitives.WriteInt32BigEndian(ihdr.Slice(4, 4), height);
            ihdr[8] = 8; // 8 bits per channel
            ihdr[9] = 6; // RGBA
            ihdr[10] = 0;
            ihdr[11] = 0;
            ihdr[12] = 0;
            offset += WritePngChunk(outSpan[offset..], "IHDR"u8, ihdr);
            offset += WritePngChunk(
                outSpan[offset..],
                "IDAT"u8,
                idatPayload.AsSpan());
            WritePngChunk(outSpan[offset..], "IEND"u8, ReadOnlySpan<byte>.Empty);
            return png;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedScanline);
        }
    }

    private static int WritePngChunk(
        Span<byte> destination,
        ReadOnlySpan<byte> chunkType,
        ReadOnlySpan<byte> data)
    {
        BinaryPrimitives.WriteInt32BigEndian(destination.Slice(0, 4), data.Length);
        chunkType.CopyTo(destination.Slice(4, 4));
        if (!data.IsEmpty)
        {
            data.CopyTo(destination.Slice(8, data.Length));
        }

        uint crc = ComputePngCrc32(destination.Slice(4, 4 + data.Length));
        BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(8 + data.Length, 4), crc);
        return 12 + data.Length;
    }

    private static uint ComputePngCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        uint[] table = s_pngCrc32Table;
        for (int i = 0; i < data.Length; i++)
        {
            crc = table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
