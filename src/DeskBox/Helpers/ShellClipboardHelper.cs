using System.Runtime.InteropServices;
using System.Text;
using DeskBox.Platform;

namespace DeskBox.Helpers;

public static class ShellClipboardHelper
{
    private const uint CfHdrop = 15;
    private const uint DragQueryFileCount = 0xFFFFFFFF;
    private const uint GmemMoveable = 0x0002;
    private const uint GmemZeroinit = 0x0040;
    private const uint DropEffectCopy = 1;
    private const uint DropEffectMove = 2;
    private const int DropFilesHeaderSize = 20;
    private const int ClipboardOpenAttempts = 5;
    private const int ClipboardOpenRetryDelayMs = 5;
    private const int MaxShellIdListItems = 64;
    private const uint SigdnNormalDisplay = 0;
    private const uint SigdnDesktopAbsoluteParsing = 0x80028000;
    private const uint SigdnFileSysPath = 0x80058000;

    private static readonly uint PreferredDropEffectFormat = ClipboardNativeMethods.RegisterClipboardFormat("Preferred DropEffect");
    private static readonly uint ShellIdListFormat = ClipboardNativeMethods.RegisterClipboardFormat("Shell IDList Array");

    public static bool TrySetFileDropList(IReadOnlyList<string> paths, bool cut)
    {
        var validPaths = paths
            .Where(path => !string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path)))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (validPaths.Length == 0)
        {
            return false;
        }

        if (!TryOpenClipboard())
        {
            return false;
        }

        IntPtr dropHandle = IntPtr.Zero;
        IntPtr effectHandle = IntPtr.Zero;
        try
        {
            if (!ClipboardNativeMethods.EmptyClipboard())
            {
                return false;
            }

            dropHandle = CreateDropFilesHandle(validPaths);
            if (PreferredDropEffectFormat != 0)
            {
                effectHandle = CreateDropEffectHandle(
                    cut ? DropEffectMove : DropEffectCopy);
            }

            if (ClipboardNativeMethods.SetClipboardData(CfHdrop, dropHandle) == IntPtr.Zero)
            {
                return false;
            }

            dropHandle = IntPtr.Zero;

            if (PreferredDropEffectFormat != 0 &&
                ClipboardNativeMethods.SetClipboardData(PreferredDropEffectFormat, effectHandle) ==
                    IntPtr.Zero)
            {
                return false;
            }

            effectHandle = IntPtr.Zero;
            return true;
        }
        finally
        {
            if (dropHandle != IntPtr.Zero)
            {
                ClipboardNativeMethods.GlobalFree(dropHandle);
            }

            if (effectHandle != IntPtr.Zero)
            {
                ClipboardNativeMethods.GlobalFree(effectHandle);
            }

            ClipboardNativeMethods.CloseClipboard();
        }
    }

    public static bool HasFileDropList()
    {
        return ClipboardNativeMethods.IsClipboardFormatAvailable(CfHdrop);
    }

    public static bool TryGetFileDropList(
        out string[] paths,
        out bool cut)
    {
        paths = [];
        cut = false;
        if (!HasFileDropList() || !TryOpenClipboard())
        {
            return false;
        }

        try
        {
            IntPtr dropHandle = ClipboardNativeMethods.GetClipboardData(CfHdrop);
            if (dropHandle == IntPtr.Zero)
            {
                return false;
            }

            uint count = ClipboardNativeMethods.DragQueryFile(
                dropHandle,
                DragQueryFileCount,
                null,
                0);
            if (count == 0 || count > int.MaxValue)
            {
                return false;
            }

            var result = new List<string>((int)count);
            for (uint index = 0; index < count; index++)
            {
                uint length = ClipboardNativeMethods.DragQueryFile(dropHandle, index, null, 0);
                if (length == 0 || length >= int.MaxValue)
                {
                    continue;
                }

                var buffer = new StringBuilder(checked((int)length + 1));
                if (ClipboardNativeMethods.DragQueryFile(
                        dropHandle,
                        index,
                        buffer,
                        (uint)buffer.Capacity) == 0)
                {
                    continue;
                }

                string path = buffer.ToString();
                if (!string.IsNullOrWhiteSpace(path))
                {
                    result.Add(path);
                }
            }

            paths = result
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            cut = ReadPreferredDropEffect() is uint effect &&
                (effect & DropEffectMove) != 0;
            return paths.Length > 0;
        }
        finally
        {
            ClipboardNativeMethods.CloseClipboard();
        }
    }

    /// <summary>
    /// Whether the clipboard carries a CFSTR_SHELLIDLIST payload (namespace
    /// objects such as This PC or the Recycle Bin never appear as CF_HDROP).
    /// </summary>
    public static bool HasShellIdList()
    {
        return ShellIdListFormat != 0 &&
            ClipboardNativeMethods.IsClipboardFormatAvailable(ShellIdListFormat);
    }

    /// <summary>
    /// Reads the CFSTR_SHELLIDLIST clipboard format (a CIDA structure) and
    /// resolves every child PIDL into its names. Virtual items such as This PC
    /// yield a parsing name but no file-system path; real items yield both.
    /// </summary>
    public static bool TryGetShellIdListItems(
        out IReadOnlyList<ShellNamespaceClipboardItem> items)
    {
        items = [];
        if (!HasShellIdList() || !TryOpenClipboard())
        {
            return false;
        }

        byte[] buffer;
        try
        {
            IntPtr idListHandle = ClipboardNativeMethods.GetClipboardData(ShellIdListFormat);
            if (idListHandle == IntPtr.Zero)
            {
                return false;
            }

            long size = ClipboardNativeMethods.GlobalSize(idListHandle).ToInt64();
            if (size < sizeof(uint) + (2 * sizeof(uint)) || size > int.MaxValue)
            {
                return false;
            }

            IntPtr pointer = ClipboardNativeMethods.GlobalLock(idListHandle);
            if (pointer == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                buffer = new byte[size];
                Marshal.Copy(pointer, buffer, 0, (int)size);
            }
            finally
            {
                ClipboardNativeMethods.GlobalUnlock(idListHandle);
            }
        }
        finally
        {
            ClipboardNativeMethods.CloseClipboard();
        }

        // All Shell name resolution happens after the clipboard is closed so
        // slow COM calls never hold the system clipboard open.
        if (!ShellIdListArrayParser.TryParse(buffer, out ShellIdListArrayParser.CidaLayout layout))
        {
            return false;
        }

        items = ResolveShellIdListItems(buffer, layout);
        return items.Count > 0;
    }

    /// <summary>
    /// Resolves each validated CIDA entry against the Shell: the parent and
    /// child PIDLs live inside the pinned copy of the clipboard payload, and
    /// ILCombine produces the absolute PIDL the name queries need.
    /// </summary>
    private static unsafe IReadOnlyList<ShellNamespaceClipboardItem> ResolveShellIdListItems(
        byte[] buffer,
        ShellIdListArrayParser.CidaLayout layout)
    {
        var items = new List<ShellNamespaceClipboardItem>();
        var seenParsingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        fixed (byte* pinned = buffer)
        {
            IntPtr parentPidl = new IntPtr(pinned + layout.ParentOffset);
            foreach (int childOffset in layout.ChildOffsets)
            {
                IntPtr childPidl = new IntPtr(pinned + childOffset);
                IntPtr absolutePidl = OleDropTargetNativeMethods.ILCombine(parentPidl, childPidl);
                if (absolutePidl == IntPtr.Zero)
                {
                    continue;
                }

                try
                {
                    string parsingName = ReadShellIdListName(
                        absolutePidl,
                        SigdnDesktopAbsoluteParsing);
                    if (string.IsNullOrWhiteSpace(parsingName) ||
                        !seenParsingNames.Add(parsingName))
                    {
                        continue;
                    }

                    string displayName = ReadShellIdListName(
                        absolutePidl,
                        SigdnNormalDisplay);
                    if (string.IsNullOrWhiteSpace(displayName))
                    {
                        displayName = parsingName;
                    }

                    items.Add(new ShellNamespaceClipboardItem(
                        parsingName,
                        displayName,
                        ReadShellIdListName(absolutePidl, SigdnFileSysPath)));
                }
                finally
                {
                    OleDropTargetNativeMethods.ILFree(absolutePidl);
                }
            }
        }

        if (items.Count > MaxShellIdListItems)
        {
            items.RemoveRange(MaxShellIdListItems, items.Count - MaxShellIdListItems);
        }

        return items;
    }

    private static string ReadShellIdListName(IntPtr itemIdList, uint nameType)
    {
        IntPtr value = IntPtr.Zero;
        int hresult = OleDropTargetNativeMethods.SHGetNameFromIDList(
            itemIdList,
            nameType,
            out value);
        if (hresult < 0 || value == IntPtr.Zero)
        {
            return string.Empty;
        }

        try
        {
            return Marshal.PtrToStringUni(value) ?? string.Empty;
        }
        finally
        {
            OleDropTargetNativeMethods.CoTaskMemFree(value);
        }
    }

    /// <summary>
    /// One clipboard namespace object: the parsing name identifies it for
    /// shortcut creation, the display name names the shortcut file, and the
    /// file-system path is empty for purely virtual items (This PC, Recycle
    /// Bin, Control Panel, ...).
    /// </summary>
    public sealed record ShellNamespaceClipboardItem(
        string ParsingName,
        string DisplayName,
        string FileSystemPath);

    private static IntPtr CreateDropFilesHandle(IReadOnlyList<string> paths)
    {
        byte[] payload = CreateDropFilesPayload(paths);
        IntPtr handle = ClipboardNativeMethods.GlobalAlloc(
            GmemMoveable | GmemZeroinit,
            (nuint)payload.Length);
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException(Localize("Widget.Error.ClipboardAllocate"));
        }

        IntPtr pointer = ClipboardNativeMethods.GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            ClipboardNativeMethods.GlobalFree(handle);
            throw new InvalidOperationException(Localize("Widget.Error.ClipboardWrite"));
        }

        try
        {
            Marshal.Copy(payload, 0, pointer, payload.Length);
        }
        finally
        {
            ClipboardNativeMethods.GlobalUnlock(handle);
        }

        return handle;
    }

    private static byte[] CreateDropFilesPayload(IReadOnlyList<string> paths)
    {
        string pathList = string.Join('\0', paths) + "\0\0";
        byte[] pathBytes = Encoding.Unicode.GetBytes(pathList);
        byte[] payload = new byte[DropFilesHeaderSize + pathBytes.Length];

        // DROPFILES is fixed at 20 bytes: DWORD pFiles, POINT, BOOL fNC,
        // BOOL fWide. Writing the native layout explicitly avoids runtime
        // bool-marshalling differences that can truncate a multi-file list.
        BitConverter.GetBytes((uint)DropFilesHeaderSize).CopyTo(payload, 0);
        BitConverter.GetBytes(1).CopyTo(payload, 16);
        pathBytes.CopyTo(payload, DropFilesHeaderSize);
        return payload;
    }

    private static uint? ReadPreferredDropEffect()
    {
        if (PreferredDropEffectFormat == 0 ||
            !ClipboardNativeMethods.IsClipboardFormatAvailable(PreferredDropEffectFormat))
        {
            return null;
        }

        IntPtr effectHandle = ClipboardNativeMethods.GetClipboardData(PreferredDropEffectFormat);
        if (effectHandle == IntPtr.Zero)
        {
            return null;
        }

        IntPtr pointer = ClipboardNativeMethods.GlobalLock(effectHandle);
        if (pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return unchecked((uint)Marshal.ReadInt32(pointer));
        }
        finally
        {
            ClipboardNativeMethods.GlobalUnlock(effectHandle);
        }
    }

    private static bool TryOpenClipboard()
    {
        for (int attempt = 0; attempt < ClipboardOpenAttempts; attempt++)
        {
            if (ClipboardNativeMethods.OpenClipboard(IntPtr.Zero))
            {
                return true;
            }

            if (attempt + 1 < ClipboardOpenAttempts)
            {
                Thread.Sleep(ClipboardOpenRetryDelayMs);
            }
        }

        return false;
    }

    private static IntPtr CreateDropEffectHandle(uint effect)
    {
        IntPtr handle = ClipboardNativeMethods.GlobalAlloc(GmemMoveable | GmemZeroinit, sizeof(uint));
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException(Localize("Widget.Error.ClipboardAllocate"));
        }

        IntPtr pointer = ClipboardNativeMethods.GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            ClipboardNativeMethods.GlobalFree(handle);
            throw new InvalidOperationException(Localize("Widget.Error.ClipboardWrite"));
        }

        try
        {
            Marshal.WriteInt32(pointer, unchecked((int)effect));
        }
        finally
        {
            ClipboardNativeMethods.GlobalUnlock(handle);
        }

        return handle;
    }

    private static string Localize(string key)
    {
        try
        {
            return global::DeskBox.App.Current?.LocalizationService?.T(key) ?? key;
        }
        catch
        {
            return key;
        }
    }

    // Win32 clipboard/global-memory/HDROP entry points live in
    // DeskBox.Platform.ClipboardNativeMethods.
}

/// <summary>
/// Pure parser for the CFSTR_SHELLIDLIST clipboard payload (the CIDA
/// structure: a UINT cidl followed by cidl+1 UINT offsets from the start of
/// the structure - offset 0 is the parent folder's full PIDL, the rest are
/// child PIDLs relative to it). Pure by design: byte layout and PIDL bounds
/// are validated without native calls so tests can feed constructed buffers.
/// </summary>
internal static class ShellIdListArrayParser
{
    private const int MaxItems = 64;

    /// <summary>Validated byte offsets into the payload buffer.</summary>
    internal readonly record struct CidaLayout(int ParentOffset, int[] ChildOffsets);

    internal static bool TryParse(ReadOnlySpan<byte> buffer, out CidaLayout layout)
    {
        layout = default;
        // Minimum: cidl plus the parent offset and one child offset.
        if (buffer.Length < sizeof(uint) + (2 * sizeof(uint)))
        {
            return false;
        }

        uint childCount = BitConverter.ToUInt32(buffer);
        if (childCount == 0 || childCount > MaxItems)
        {
            return false;
        }

        long offsetCount = (long)childCount + 1;
        long headerSize = sizeof(uint) + (offsetCount * sizeof(uint));
        if (headerSize > buffer.Length)
        {
            return false;
        }

        int parentOffset = BitConverter.ToInt32(buffer.Slice(sizeof(uint)));
        if (!IsTerminatedPidlAt(buffer, parentOffset, headerSize))
        {
            return false;
        }

        var childOffsets = new int[childCount];
        for (int index = 0; index < childCount; index++)
        {
            int offsetPosition = checked(
                sizeof(uint) + ((index + 1) * sizeof(uint)));
            int childOffset = BitConverter.ToInt32(buffer.Slice(offsetPosition));
            if (!IsTerminatedPidlAt(buffer, childOffset, headerSize))
            {
                return false;
            }

            childOffsets[index] = childOffset;
        }

        layout = new CidaLayout(parentOffset, childOffsets);
        return true;
    }

    /// <summary>
    /// Walks the ITEMIDLIST chain at <paramref name="offset"/> until the
    /// two-byte terminator; the chain must stay inside the buffer and every
    /// SHITEMID must be at least as large as its own length field.
    /// </summary>
    private static bool IsTerminatedPidlAt(
        ReadOnlySpan<byte> buffer,
        int offset,
        long headerSize)
    {
        if (offset < headerSize || offset >= buffer.Length)
        {
            return false;
        }

        long cursor = offset;
        while (cursor <= buffer.Length - sizeof(ushort))
        {
            ushort itemSize = BitConverter.ToUInt16(buffer.Slice((int)cursor));
            if (itemSize == 0)
            {
                return true;
            }

            if (itemSize < sizeof(ushort) || cursor > buffer.Length - itemSize)
            {
                return false;
            }

            cursor += itemSize;
        }

        return false;
    }
}
