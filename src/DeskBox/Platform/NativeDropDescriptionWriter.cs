using System.Runtime.InteropServices;
using DeskBox.Helpers;

namespace DeskBox.Platform;

internal readonly record struct NativeDropDescriptionText(
    string Message,
    string Insert);

/// <summary>
/// Writes the Shell DROPDESCRIPTION format without creating a COM RCW. The
/// Explorer drag source owns the compact drag image; DeskBox only supplies the
/// stock copy/move glyph and target text that Shell renders beside it.
/// </summary>
internal static partial class NativeDropDescriptionWriter
{
    private const uint DvaspectContent = 1;
    private const uint TymedHGlobal = 1;
    private const uint GmemMoveable = 0x0002;
    private const uint GmemZeroInit = 0x0040;
    private const uint WmUser = 0x0400;
    private const uint DdwmUpdateWindow = WmUser + 3;
    private const int MaxPath = 260;
    private const int DropImageInvalid = -1;
    private const int DropImageNone = 0;
    private const int DropImageCopy = 1;
    private const int DropImageMove = 2;
    private const int DropImageLink = 3;
    private const int DropDescriptionMessageOffset = sizeof(int);
    private const int DropDescriptionInsertOffset =
        DropDescriptionMessageOffset + (MaxPath * sizeof(char));
    private const int DropDescriptionSize =
        DropDescriptionInsertOffset + (MaxPath * sizeof(char));

    private static readonly ushort s_dropDescriptionFormat =
        (ushort)RegisterClipboardFormatW("DropDescription");
    private static readonly ushort s_dragWindowFormat =
        (ushort)RegisterClipboardFormatW("DragWindow");

    [LibraryImport("user32.dll", EntryPoint = "RegisterClipboardFormatW",
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterClipboardFormatW(string format);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GlobalLock(nint memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(nint memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nuint GlobalSize(nint memory);

    [LibraryImport("ole32.dll")]
    private static partial void ReleaseStgMedium(ref NativeStorageMedium medium);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(
        nint window,
        uint message,
        nuint wParam,
        nint lParam);

    internal static bool TryApply(
        nint dataObjectPointer,
        uint effect,
        NativeDropDescriptionText text)
    {
        if (dataObjectPointer == 0 ||
            string.IsNullOrWhiteSpace(text.Message))
        {
            return false;
        }

        int imageType = effect switch
        {
            NativeDropEffectPolicy.Copy => DropImageCopy,
            NativeDropEffectPolicy.Move => DropImageMove,
            NativeDropEffectPolicy.Link => DropImageLink,
            _ => DropImageNone
        };
        return TryWrite(
            dataObjectPointer,
            imageType,
            text.Message,
            text.Insert);
    }

    internal static bool TryClear(nint dataObjectPointer) =>
        dataObjectPointer != 0 &&
        TryWrite(
            dataObjectPointer,
            DropImageInvalid,
            string.Empty,
            string.Empty);

    private static bool TryWrite(
        nint dataObjectPointer,
        int imageType,
        string message,
        string insert)
    {
        nint globalMemory = GlobalAlloc(
            GmemMoveable | GmemZeroInit,
            DropDescriptionSize);
        if (globalMemory == 0)
        {
            return false;
        }

        var medium = new NativeStorageMedium
        {
            MediumType = TymedHGlobal,
            Content = globalMemory,
            ReleaseUnknown = 0
        };
        bool ownershipTransferred = false;
        try
        {
            nint payload = GlobalLock(globalMemory);
            if (payload == 0)
            {
                return false;
            }

            try
            {
                Marshal.WriteInt32(payload, imageType);
                WriteBoundedUnicode(
                    message,
                    payload + DropDescriptionMessageOffset);
                WriteBoundedUnicode(
                    insert,
                    payload + DropDescriptionInsertOffset);
            }
            finally
            {
                _ = GlobalUnlock(globalMemory);
            }

            var format = new NativeFormatEtc
            {
                ClipboardFormat = s_dropDescriptionFormat,
                TargetDevice = 0,
                Aspect = DvaspectContent,
                Index = -1,
                MediumType = TymedHGlobal
            };
            var dataObject = new NativeOleDataObject(dataObjectPointer);
            int result = dataObject.SetData(
                ref format,
                ref medium,
                release: true);
            ownershipTransferred = result >= 0;
            if (!ownershipTransferred)
            {
                return false;
            }

            NotifyDragWindow(dataObject);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (!ownershipTransferred && medium.Content != 0)
            {
                ReleaseStgMedium(ref medium);
            }
        }
    }

    private static void WriteBoundedUnicode(string value, nint destination)
    {
        int length = Math.Min(value.Length, MaxPath - 1);
        if (length <= 0)
        {
            return;
        }

        char[] characters = value.AsSpan(0, length).ToArray();
        Marshal.Copy(characters, 0, destination, characters.Length);
    }

    private static void NotifyDragWindow(NativeOleDataObject dataObject)
    {
        var format = new NativeFormatEtc
        {
            ClipboardFormat = s_dragWindowFormat,
            TargetDevice = 0,
            Aspect = DvaspectContent,
            Index = -1,
            MediumType = TymedHGlobal
        };
        if (dataObject.GetData(ref format, out NativeStorageMedium medium) < 0)
        {
            return;
        }

        try
        {
            if (medium.MediumType != TymedHGlobal ||
                medium.Content == 0 ||
                GlobalSize(medium.Content) < sizeof(uint))
            {
                return;
            }

            nint payload = GlobalLock(medium.Content);
            if (payload == 0)
            {
                return;
            }

            nint dragWindow;
            try
            {
                dragWindow = new IntPtr(
                    unchecked((long)(uint)Marshal.ReadInt32(payload)));
            }
            finally
            {
                _ = GlobalUnlock(medium.Content);
            }

            if (dragWindow != 0)
            {
                _ = PostMessageW(
                    dragWindow,
                    DdwmUpdateWindow,
                    0,
                    0);
            }
        }
        finally
        {
            ReleaseStgMedium(ref medium);
        }
    }
}
