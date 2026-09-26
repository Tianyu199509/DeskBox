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

    private static readonly uint PreferredDropEffectFormat = ClipboardNativeMethods.RegisterClipboardFormat("Preferred DropEffect");

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
