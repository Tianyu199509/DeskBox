using System.Buffers.Binary;
using System.Runtime.InteropServices;
using DeskBox.Platform;

namespace DeskBox.Helpers;

/// <summary>
/// Builds a Shell <c>IDataObject</c> carrying <c>CF_HDROP</c> for drops whose
/// original OLE data object is no longer alive: drops that arrive through the
/// WinUI routed pipeline, right-button-drag choices resolved after release,
/// and DeskBox-internal item drags. The object is the Shell's own
/// (SHCreateDataObject), so format enumeration and QueryGetData behave exactly
/// like a native file drop when handed to a Shell drop handler.
///
/// HDROP is a double-NUL-terminated wide path list, not a command line, so no
/// quoting or escaping is involved. The caller releases the returned pointer
/// via <see cref="ShellDropDelegator.ReleaseObject"/> once the delegation (or
/// fallback) has finished. Must be used on an STA thread.
/// </summary>
internal static unsafe partial class ShellDataObjectBuilder
{
    private const ushort Cfdrop = 15; // CF_HDROP
    private const uint DvaspectContent = 1;
    private const uint TymedHGlobal = 1;
    private const uint GmemMoveable = 0x0002;
    private const uint GmemZeroInit = 0x0040;

    // DROPFILES: DWORD pFiles; POINT pt; BOOL fNC; BOOL fWide - 20 bytes on
    // every architecture. Written field by field because C# sizeof on a struct
    // with a bool field disagrees with the marshaled BOOL width.
    private const int DropFilesHeaderSize = 20;

    // IID_IDataObject. The value must be 0000010E: a 0000000E prefix makes
    // SHCreateDataObject fail with E_NOINTERFACE, which silently disabled every
    // synthesized-drop path (routed XAML drop, right-button launch, WM_DROPFILES).
    private static readonly Guid DataObjectIid = new("0000010E-0000-0000-C000-000000000046");

    // Native entry points live in DeskBox.Platform.HdropDataObjectNativeMethods.

    internal static bool TryCreateHdropDataObject(
        IReadOnlyList<string> paths,
        out nint dataObject)
    {
        dataObject = 0;
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
        if (normalized.Length == 0)
        {
            return false;
        }

        int createHResult = HdropDataObjectNativeMethods.SHCreateDataObject(
            nint.Zero,
            0,
            nint.Zero,
            nint.Zero,
            DataObjectIid,
            out nint shellDataObject);
        if (createHResult < 0 || shellDataObject == 0)
        {
            App.Log(
                "[ShortcutLaunch] SHCreateDataObject failed: " +
                $"hr=0x{createHResult:X8}.");
            return false;
        }

        try
        {
            nint hdrop = BuildHdropMemory(normalized);
            if (hdrop == 0)
            {
                return false;
            }

            var medium = new NativeStorageMedium
            {
                MediumType = TymedHGlobal,
                Content = hdrop,
                ReleaseUnknown = 0
            };
            var format = new NativeFormatEtc
            {
                ClipboardFormat = Cfdrop,
                TargetDevice = 0,
                Aspect = DvaspectContent,
                Index = -1,
                MediumType = TymedHGlobal
            };
            var receiver = new NativeOleDataObject(shellDataObject);
            int setResult = receiver.SetData(
                ref format,
                ref medium,
                release: true);
            if (setResult < 0)
            {
                // The Shell object rejected the medium, so its ownership never
                // transferred and the HDROP memory is still ours to free.
                HdropDataObjectNativeMethods.GlobalFree(medium.Content);
                App.Log(
                    "[ShortcutLaunch] SetData(CF_HDROP) failed: " +
                    $"hr=0x{setResult:X8}.");
                return false;
            }

            dataObject = shellDataObject;
            shellDataObject = 0;
            return true;
        }
        finally
        {
            if (shellDataObject != 0)
            {
                ShellDropDelegator.ReleaseObject(shellDataObject);
            }
        }
    }

    private static nint BuildHdropMemory(string[] paths)
    {
        byte[] payload = BuildHdropBytes(paths);
        nint memory = HdropDataObjectNativeMethods.GlobalAlloc(
            GmemMoveable | GmemZeroInit,
            (nuint)payload.Length);
        if (memory == 0)
        {
            App.Log("[ShortcutLaunch] GlobalAlloc for CF_HDROP failed.");
            return 0;
        }

        nint locked = HdropDataObjectNativeMethods.GlobalLock(memory);
        if (locked == 0)
        {
            HdropDataObjectNativeMethods.GlobalFree(memory);
            return 0;
        }

        try
        {
            Marshal.Copy(payload, 0, locked, payload.Length);
            return memory;
        }
        finally
        {
            _ = HdropDataObjectNativeMethods.GlobalUnlock(memory);
        }
    }

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
