namespace DeskBox.Services;

/// <summary>
/// Repairs virtual-drop file names when an OLE or WinUI drag source supplies
/// only a stem. Browsers commonly do this for image resources even though the
/// stream itself contains a complete, identifiable file.
/// </summary>
internal static class VirtualDropFileNameResolver
{
    private const int HeaderLength = 64;

    /// <summary>
    /// Appends a conservatively inferred extension only when <paramref name="path" />
    /// has no extension already. Unknown content deliberately keeps its original
    /// name instead of guessing an unrelated file type.
    /// </summary>
    internal static string AddMissingExtensionFromContent(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            Path.HasExtension(path) ||
            !File.Exists(path))
        {
            return path;
        }

        try
        {
            byte[] header = new byte[HeaderLength];
            int headerLength = ReadHeader(path, header);
            string? extension = TryGetExtension(header.AsSpan(0, headerLength));
            if (string.IsNullOrWhiteSpace(extension))
            {
                return path;
            }

            string destinationPath = FileService.GetAvailablePath(path + extension);
            File.Move(path, destinationPath);
            return destinationPath;
        }
        catch
        {
            // A failed best-effort rename must never make a successfully
            // materialized drag payload unavailable to the import pipeline.
            return path;
        }
    }

    internal static string? TryGetExtension(ReadOnlySpan<byte> header)
    {
        if (HasPrefix(header, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A))
        {
            return ".png";
        }

        if (HasPrefix(header, 0xFF, 0xD8, 0xFF))
        {
            return ".jpg";
        }

        if (HasPrefix(header, 0x47, 0x49, 0x46, 0x38, 0x37, 0x61) ||
            HasPrefix(header, 0x47, 0x49, 0x46, 0x38, 0x39, 0x61))
        {
            return ".gif";
        }

        if (HasPrefix(header, 0x42, 0x4D))
        {
            return ".bmp";
        }

        if (HasPrefix(header, 0x00, 0x00, 0x01, 0x00) ||
            HasPrefix(header, 0x00, 0x00, 0x02, 0x00))
        {
            return ".ico";
        }

        if (HasPrefix(header, 0x49, 0x49, 0x2A, 0x00) ||
            HasPrefix(header, 0x4D, 0x4D, 0x00, 0x2A))
        {
            return ".tif";
        }

        if (header.Length >= 12 &&
            HasAsciiAt(header, 0, "RIFF") &&
            HasAsciiAt(header, 8, "WEBP"))
        {
            return ".webp";
        }

        if (header.Length >= 12 &&
            HasAsciiAt(header, 4, "ftyp") &&
            (HasAsciiAt(header, 8, "avif") || HasAsciiAt(header, 8, "avis")))
        {
            return ".avif";
        }

        if (header.Length >= 12 &&
            HasAsciiAt(header, 4, "ftyp") &&
            (HasAsciiAt(header, 8, "heic") ||
             HasAsciiAt(header, 8, "heix") ||
             HasAsciiAt(header, 8, "hevc") ||
             HasAsciiAt(header, 8, "hevx") ||
             HasAsciiAt(header, 8, "mif1") ||
             HasAsciiAt(header, 8, "msf1")))
        {
            return ".heic";
        }

        if (HasAsciiAt(header, 0, "%PDF-"))
        {
            return ".pdf";
        }

        return null;
    }

    private static int ReadHeader(string path, byte[] buffer)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = stream.Read(buffer, totalRead, buffer.Length - totalRead);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        return totalRead;
    }

    private static bool HasPrefix(ReadOnlySpan<byte> header, params byte[] prefix)
    {
        return header.Length >= prefix.Length &&
               header[..prefix.Length].SequenceEqual(prefix);
    }

    private static bool HasAsciiAt(ReadOnlySpan<byte> header, int offset, string value)
    {
        if (offset < 0 || header.Length - offset < value.Length)
        {
            return false;
        }

        for (int index = 0; index < value.Length; index++)
        {
            if (header[offset + index] != value[index])
            {
                return false;
            }
        }

        return true;
    }
}
