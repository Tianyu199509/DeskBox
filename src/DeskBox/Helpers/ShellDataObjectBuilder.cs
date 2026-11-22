using System.Buffers.Binary;

namespace DeskBox.Helpers;

/// <summary>
/// Serializes <c>CF_HDROP</c> byte payloads (<c>DROPFILES</c> header followed by
/// double-NUL-terminated UTF-16LE paths).
/// </summary>
internal static class ShellDataObjectBuilder
{
    // DROPFILES: DWORD pFiles; POINT pt; BOOL fNC; BOOL fWide - 20 bytes on
    // every architecture. Written field by field because C# sizeof on a struct
    // with a bool field disagrees with the marshaled BOOL width.
    private const int DropFilesHeaderSize = 20;

    /// <summary>
    /// Serializes a DROPFILES header followed by the double-NUL-terminated
    /// wide path list. Internal for byte-level tests: HDROP is a raw path
    /// list, so directory paths keep their trailing backslash verbatim.
    /// </summary>
    internal static byte[] BuildHdropBytes(IReadOnlyList<string> paths)
    {
        string[] normalized = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path =>
            {
                try
                {
                    return Path.GetFullPath(path);
                }
                catch
                {
                    return string.Empty;
                }
            })
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        int characterCount = normalized.Sum(path => path.Length + 1) + 1;
        var payload = new byte[DropFilesHeaderSize + (characterCount * sizeof(char))];
        // The object-based Marshal.WriteInt32 overloads are both obsolete and
        // AOT-incompatible (IL3050); write the header the same way the path
        // bytes below are written - explicitly little-endian.
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0, 4), DropFilesHeaderSize);  // pFiles
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4, 4), 0);                   // pt.x
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(8, 4), 0);                   // pt.y
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(12, 4), 0);                  // fNC
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(16, 4), 1);                  // fWide

        int offset = DropFilesHeaderSize;
        foreach (string path in normalized)
        {
            foreach (char character in path)
            {
                payload[offset++] = (byte)character;
                payload[offset++] = (byte)(character >> 8);
            }

            payload[offset++] = 0; // path terminator
            payload[offset++] = 0;
        }

        payload[offset++] = 0; // list terminator
        payload[offset] = 0;
        return payload;
    }
}
